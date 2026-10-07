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
/// litd is on this deployment, by either fragment. Not the same as reachable - an upstream install
/// still needs a UI password before anything can be asked of it.
/// </param>
/// <param name="Running">litd answered a gRPC call.</param>
/// <param name="Backend">The Lightning implementation this deployment runs, and whether litd can use it.</param>
/// <param name="SubServers">litd's own report on each sub-server it manages. Empty unless litd answered.</param>
/// <param name="LndState">LND's wallet/RPC state as litd sees it, when litd could report it.</param>
/// <param name="Version">litd's own version, when litd could report it.</param>
/// <param name="Error">Why litd could not be reached or queried. Null when it was.</param>
/// <param name="LogAvailable">litd has written a log file that can be downloaded.</param>
/// <param name="Connection">Which litd install was found, and whether it can be talked to.</param>
public record LitdStatus(
    bool Installed,
    bool Running,
    LightningBackend Backend,
    IReadOnlyList<SubServer> SubServers,
    WalletState? LndState,
    string? Version,
    string? Error,
    bool LogAvailable,
    LitdConnectionInfo Connection)
{
    /// <summary>
    /// litd is here but was installed by a fragment that predates the data-volume mount, so this
    /// plugin cannot reach it. Fixed by updating btcpayserver-docker, not by anything on this page.
    /// </summary>
    public bool NeedsUpdate => Connection.Mode is LitdConnectionMode.Legacy;

    /// <summary>True once litd is up and nothing it is running is in an error state.</summary>
    public bool Healthy => Running && Error is null && !SubServers.Any(s => s.HasError);

    /// <summary>
    /// No litd on this deployment at all, by either fragment - the only state in which there is
    /// something to introduce rather than something to report on.
    /// </summary>
    /// <remarks>
    /// Deliberately not just <c>!Installed</c>: a deployment running the upstream fragment has litd,
    /// this plugin just cannot see into it, and pitching litd to someone already running it reads as a
    /// bug. Exactly one of <see cref="Installed"/> and this is ever true.
    /// </remarks>
    public bool NotFound => Connection.Mode is LitdConnectionMode.None;
}

public class LitdStatusService(
    LitdClient client,
    LitdPaths paths,
    LightningBackendDetector backendDetector,
    LitdConnection connection)
{
    public async Task<LitdStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var backend = backendDetector.Detect();
        var info = connection.Describe();
        var installed = info.Mode is not LitdConnectionMode.None;
        var logAvailable = info.CanReadLogs && paths.LogFile is not null;

        if (!installed)
            return new LitdStatus(false, false, backend, [], null, null, null, false, info);

        // A legacy install has no macaroon and no certificate within reach, so there is nothing to ask
        // it. Reported as installed-but-not-running rather than attempted and failed, because the page
        // has a specific thing to say about it and a connection error would bury that.
        if (info.Mode is LitdConnectionMode.Legacy)
            return new LitdStatus(true, false, backend, [], null, null, null, false, info);

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

            // Same tolerance, for the same reason: the version is a nicety, and litd refusing it must
            // not cost the sub-server report that is the point of this page.
            string? version = null;
            try
            {
                version = await client.GetVersionAsync(cancellationToken);
            }
            catch (RpcException)
            {
            }
            catch (LitdNotReadyException)
            {
            }

            return new LitdStatus(true, true, backend, subServers, lndState, version, null, logAvailable, info);
        }
        catch (RpcException ex)
        {
            return new LitdStatus(true, false, backend, [], null, null, LitdClient.Explain(ex), logAvailable, info);
        }
        catch (LitdNotReadyException ex)
        {
            return new LitdStatus(true, false, backend, [], null, null, ex.Message, logAvailable, info);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return new LitdStatus(true, false, backend, [], null, null, ex.Message, logAvailable, info);
        }
    }

    private static List<SubServer> Flatten(SubServerStatusResp response) =>
        response.SubServers
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => new SubServer(
                entry.Key, entry.Value.Disabled, entry.Value.Running, entry.Value.Error, entry.Value.CustomStatus))
            .ToList();
}
