using BTCPayServer.Configuration;
using Grpc.Core;
using Litrpc;
using Lnrpc;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.LightningTerminal.Services;

/// <param name="Name">litd's own name for the sub-server, e.g. <c>lnd</c>, <c>loop</c>, <c>faraday</c>.</param>
/// <param name="Disabled">Shipped with litd but switched off for this deployment.</param>
/// <param name="Running">Currently up and serving.</param>
/// <param name="Error">Why it failed to start, empty when it did not fail.</param>
/// <param name="CustomStatus">A sub-server-specific state that is neither running, disabled, nor errored.</param>
public record SubServer(string Name, bool Disabled, bool Running, string Error, string CustomStatus)
{
    public bool HasError => !string.IsNullOrEmpty(Error);
}

/// <summary>Everything the plugin can tell an administrator about litd on this deployment.</summary>
/// <param name="Installed">
/// litd's data directory is mounted into this container, so the generated fragment is part of the
/// running Compose stack.
/// </param>
/// <param name="Running">litd answered a gRPC call.</param>
/// <param name="Backend">The Lightning implementation this deployment runs, and whether litd can use it.</param>
/// <param name="SubServers">litd's own report on each sub-server it manages. Empty unless litd answered.</param>
/// <param name="LndState">LND's wallet/RPC state as litd sees it, when litd could report it.</param>
/// <param name="Error">Why litd could not be reached or queried. Null when it was.</param>
/// <param name="LogAvailable">litd has written a log file that can be downloaded.</param>
/// <param name="UpstreamUrl">
/// Where litd's web UI is served, when this deployment looks like it runs btcpayserver-docker's own
/// Lightning Terminal fragment instead of the one this plugin generates. Null otherwise.
/// </param>
public record LitdStatus(
    bool Installed,
    bool Running,
    LightningBackend Backend,
    IReadOnlyList<SubServer> SubServers,
    WalletState? LndState,
    string? Error,
    bool LogAvailable,
    Uri? UpstreamUrl)
{
    /// <summary>True once litd is up and nothing it is running is in an error state.</summary>
    public bool Healthy => Running && Error is null && !SubServers.Any(s => s.HasError);

    /// <summary>
    /// litd is on this deployment, but through the upstream fragment, so it is not reachable by this
    /// plugin and the two fragments would collide if both were selected.
    /// </summary>
    public bool UpstreamOnly => !Installed && UpstreamUrl is not null;
}

public class LitdStatusService(
    LitdClient client,
    LitdPaths paths,
    LightningBackendDetector backendDetector,
    IOptions<ExternalServicesOptions> externalServices)
{
    /// <summary>
    /// The name btcpayserver-docker's own Lightning Terminal fragment registers in
    /// <c>BTCPAY_EXTERNALSERVICES</c>. Its presence is the only host-free signal that the upstream
    /// fragment - rather than this plugin's - is what put litd on this deployment.
    /// </summary>
    private const string UpstreamExternalServiceName = "Lightning Terminal";

    public async Task<LitdStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var backend = backendDetector.Detect();
        var installed = paths.DataDirectoryMounted;
        var logAvailable = paths.LogFile is not null;
        externalServices.Value.OtherExternalServices.TryGetValue(UpstreamExternalServiceName, out var upstreamUrl);

        if (!installed)
            return new LitdStatus(false, false, backend, [], null, null, false, upstreamUrl);

        try
        {
            var subServers = Flatten(await client.GetSubServerStatusAsync(cancellationToken));

            // Asked for separately and tolerantly: litd answers Status without a macaroon but proxies
            // State through to LND, so a locked or unreachable LND must not cost us the sub-server
            // report that would explain why.
            WalletState? lndState = null;
            try
            {
                lndState = await client.GetLndStateAsync(cancellationToken);
            }
            catch (RpcException)
            {
            }

            return new LitdStatus(true, true, backend, subServers, lndState, null, logAvailable, upstreamUrl);
        }
        catch (RpcException ex)
        {
            return new LitdStatus(true, false, backend, [], null, LitdClient.Explain(ex), logAvailable, upstreamUrl);
        }
        catch (LitdNotReadyException ex)
        {
            return new LitdStatus(true, false, backend, [], null, ex.Message, logAvailable, upstreamUrl);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return new LitdStatus(true, false, backend, [], null, ex.Message, logAvailable, upstreamUrl);
        }
    }

    private static List<SubServer> Flatten(SubServerStatusResp response) =>
        response.SubServers
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => new SubServer(
                entry.Key, entry.Value.Disabled, entry.Value.Running, entry.Value.Error, entry.Value.CustomStatus))
            .ToList();
}
