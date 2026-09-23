using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Grpc.Net.Client;
using Lnrpc;

namespace BTCPayServer.Plugins.LightningTerminal.Services;

/// <summary>
/// gRPC client for BTCPay's bundled LND, used for the one thing litd cannot be asked to do: bake a
/// macaroon.
/// </summary>
/// <remarks>
/// <para>
/// Talks to LND directly rather than through litd's proxy, and needs no change to the generated
/// fragment: btcpayserver-docker already mounts <c>lnd_bitcoin_datadir</c> into the BTCPay container
/// (that is how BTCPay drives the internal node), and LND already listens for gRPC on
/// <c>lnd_bitcoin:10009</c>.
/// </para>
/// <para>
/// Unlike litd, LND's certificate does cover the name we dial - bitcoin-lnd.yml passes
/// <c>tlsextradomain=lnd_bitcoin</c> - so this validates the chain against that certificate by name
/// rather than pinning it by value.
/// </para>
/// </remarks>
public sealed class LndClient(TerminalOptions options) : IDisposable
{
    private readonly Lock _lock = new();
    private GrpcChannel? _channel;
    private string? _channelCertificateThumbprint;

    /// <summary>
    /// Asks LND for a macaroon with the given root key and permissions, hex encoded.
    /// </summary>
    /// <remarks>
    /// <c>AllowExternalPermissions</c> because the account permission set is litd's, and LND rejects
    /// entities it does not recognise otherwise - litd passes the same flag when it bakes.
    /// </remarks>
    public async Task<string> BakeMacaroonAsync(
        ulong rootKeyId,
        IReadOnlyList<(string Entity, string Action)> permissions,
        CancellationToken cancellationToken)
    {
        // Fully qualified: bare "Lightning" resolves to BTCPayServer.Lightning from the host assembly.
        var client = new Lnrpc.Lightning.LightningClient(GetChannel());
        var request = new BakeMacaroonRequest
        {
            RootKeyId = rootKeyId,
            AllowExternalPermissions = true
        };
        foreach (var (entity, action) in permissions)
            request.Permissions.Add(new MacaroonPermission { Entity = entity, Action = action });

        var response = await client.BakeMacaroonAsync(
            request, Authenticated(), DateTime.UtcNow.AddSeconds(30), cancellationToken);
        return response.Macaroon;
    }

    private Metadata Authenticated()
    {
        var macaroonFile = options.LndMacaroonFile;
        if (!File.Exists(macaroonFile))
            throw new LitdNotReadyException(
                $"LND's admin macaroon is not readable at {macaroonFile}. BTCPay mounts it from the " +
                "deployment's LND, so this usually means the bundled LND is not running.");

        byte[] macaroon;
        try
        {
            macaroon = File.ReadAllBytes(macaroonFile);
        }
        catch (IOException ex)
        {
            throw new LitdNotReadyException($"Could not read LND's macaroon at {macaroonFile}: {ex.Message}");
        }

        return new Metadata { { "macaroon", Convert.ToHexString(macaroon).ToLowerInvariant() } };
    }

    private GrpcChannel GetChannel()
    {
        var certificateFile = options.LndTlsCertificateFile;
        if (!File.Exists(certificateFile))
            throw new LitdNotReadyException(
                $"LND's TLS certificate is not readable at {certificateFile}.");

        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadCertificateFromFile(certificateFile);
        }
        catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException)
        {
            throw new LitdNotReadyException($"Could not read LND's TLS certificate: {ex.Message}");
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

            // LND's certificate is self-signed, so it has to be trusted explicitly - but it does carry
            // the name we dial, so the name is still checked rather than waved through.
            var handler = new SocketsHttpHandler();
            handler.SslOptions.RemoteCertificateValidationCallback = (_, presented, chain, _) =>
                presented is not null && chain is not null && ValidatesAgainst(certificate, presented, chain);

            _channel = GrpcChannel.ForAddress(
                $"https://{options.LndRpcHost}:{options.LndRpcPort}",
                new GrpcChannelOptions { HttpHandler = handler, DisposeHttpClient = true });
            return _channel;
        }
    }

    private static bool ValidatesAgainst(X509Certificate2 trusted, X509Certificate presented, X509Chain chain)
    {
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Clear();
        chain.ChainPolicy.CustomTrustStore.Add(trusted);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        return chain.Build(X509CertificateLoader.LoadCertificate(presented.GetRawCertData()));
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _channel?.Dispose();
            _channel = null;
        }
    }
}
