using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Grpc.Net.Client;
using Litrpc;
using Lnrpc;

namespace BTCPayServer.Plugins.LightningTerminal.Services;

/// <summary>Thrown when litd's data directory is present but litd has not yet produced what a call needs.</summary>
public class LitdNotReadyException(string message) : Exception(message);

/// <summary>
/// gRPC client for the litd container, talking to it over the deployment's internal Docker network.
/// </summary>
/// <remarks>
/// litd's TLS certificate is self-signed and its SANs cover the container's own hostname, never the
/// Compose service name we dial, so the certificate is pinned by value instead of validated by name:
/// we compare what the server presents against the <c>tls.cert</c> in litd's data directory, which is
/// the same trust anchor litcli uses. The channel is cached per certificate, so litd regenerating an
/// expired certificate transparently rebuilds it.
/// </remarks>
public sealed class LitdClient(TerminalOptions options, LitdPaths paths) : IDisposable
{
    private readonly Lock _lock = new();
    private GrpcChannel? _channel;
    private string? _channelCertificateThumbprint;

    public async Task<SubServerStatusResp> GetSubServerStatusAsync(CancellationToken cancellationToken)
    {
        var client = new Litrpc.Status.StatusClient(GetChannel());
        // Deliberately no macaroon: litd serves Status unauthenticated precisely so that it can be
        // queried while sub-servers are still starting, or have failed to start at all.
        return await client.SubServerStatusAsync(
            new SubServerStatusReq(), deadline: Deadline(TimeSpan.FromSeconds(10)), cancellationToken: cancellationToken);
    }

    /// <summary>
    /// LND's own wallet/RPC state as litd sees it, which distinguishes "litd is up but LND is still
    /// locked or syncing" from "everything is running".
    /// </summary>
    public async Task<WalletState> GetLndStateAsync(CancellationToken cancellationToken)
    {
        var client = new State.StateClient(GetChannel());
        var response = await client.GetStateAsync(
            new GetStateRequest(), deadline: Deadline(TimeSpan.FromSeconds(10)), cancellationToken: cancellationToken);
        return response.State;
    }

    public async Task<Session> AddSessionAsync(string label, TimeSpan expiry, CancellationToken cancellationToken)
    {
        var client = new Sessions.SessionsClient(GetChannel());
        var request = new AddSessionRequest
        {
            Label = label,
            SessionType = SessionType.TypeMacaroonAdmin,
            ExpiryTimestampSeconds = (ulong)DateTimeOffset.UtcNow.Add(expiry).ToUnixTimeSeconds(),
            // The same default litcli uses. litd reaches out to this mailbox so that Terminal on the
            // web can meet it there, which is what removes any need to expose litd publicly.
            MailboxServerAddr = "mailbox.terminal.lightning.today:443"
        };
        var response = await client.AddSessionAsync(
            request, Authenticated(), Deadline(TimeSpan.FromSeconds(30)), cancellationToken);
        return response.Session;
    }

    public async Task<IReadOnlyList<Session>> ListSessionsAsync(CancellationToken cancellationToken)
    {
        var client = new Sessions.SessionsClient(GetChannel());
        var response = await client.ListSessionsAsync(
            new ListSessionsRequest(), Authenticated(), Deadline(TimeSpan.FromSeconds(10)), cancellationToken);
        return response.Sessions;
    }

    public async Task RevokeSessionAsync(byte[] localPublicKey, CancellationToken cancellationToken)
    {
        var client = new Sessions.SessionsClient(GetChannel());
        await client.RevokeSessionAsync(
            new RevokeSessionRequest { LocalPublicKey = Google.Protobuf.ByteString.CopyFrom(localPublicKey) },
            Authenticated(), Deadline(TimeSpan.FromSeconds(10)), cancellationToken);
    }

    private static DateTime Deadline(TimeSpan timeout) => DateTime.UtcNow.Add(timeout);

    private Metadata Authenticated()
    {
        var macaroonFile = paths.MacaroonFile
            ?? throw new LitdNotReadyException(
                "litd has not written its macaroon yet, so it cannot authenticate this request. " +
                "That normally means it is still starting up for the first time.");

        byte[] macaroon;
        try
        {
            macaroon = File.ReadAllBytes(macaroonFile);
        }
        catch (IOException ex)
        {
            throw new LitdNotReadyException($"Could not read litd's macaroon at {macaroonFile}: {ex.Message}");
        }

        return new Metadata { { "macaroon", Convert.ToHexString(macaroon).ToLowerInvariant() } };
    }

    private GrpcChannel GetChannel()
    {
        var certificateFile = paths.TlsCertificateFile
            ?? throw new LitdNotReadyException(
                "litd has not written its TLS certificate yet, so there is nothing to connect to. " +
                "That normally means it is still starting up for the first time.");

        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadCertificateFromFile(certificateFile);
        }
        catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException)
        {
            throw new LitdNotReadyException($"Could not read litd's TLS certificate at {certificateFile}: {ex.Message}");
        }

        lock (_lock)
        {
            if (_channel is not null && _channelCertificateThumbprint == certificate.Thumbprint)
            {
                certificate.Dispose();
                return _channel;
            }

            _channel?.Dispose();
            _channelCertificateThumbprint = certificate.Thumbprint;
            _channel = GrpcChannel.ForAddress($"https://{options.RpcHost}:{options.RpcPort}", new GrpcChannelOptions
            {
                HttpHandler = new SocketsHttpHandler
                {
                    SslOptions =
                    {
                        RemoteCertificateValidationCallback = (_, presented, _, _) =>
                            presented is not null && certificate.RawDataMemory.Span.SequenceEqual(presented.GetRawCertData())
                    }
                },
                DisposeHttpClient = true
            });
            return _channel;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _channel?.Dispose();
            _channel = null;
        }
    }

    /// <summary>
    /// Turns the gRPC failures this plugin can actually provoke into something an operator can read.
    /// Anything else is left alone, because a guess would be worse than the raw status.
    /// </summary>
    public static string Explain(RpcException exception) => exception.StatusCode switch
    {
        StatusCode.Unavailable =>
            "litd is not answering. Its container may still be starting, or it may have failed to start - " +
            "check the logs below.",
        StatusCode.Unauthenticated or StatusCode.PermissionDenied =>
            $"litd rejected this plugin's macaroon: {exception.Status.Detail}",
        StatusCode.DeadlineExceeded => "litd did not answer in time.",
        _ => string.IsNullOrEmpty(exception.Status.Detail) ? exception.Status.StatusCode.ToString() : exception.Status.Detail
    };
}
