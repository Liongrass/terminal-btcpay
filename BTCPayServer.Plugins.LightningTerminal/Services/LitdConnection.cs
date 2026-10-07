using BTCPayServer.Configuration;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.LightningTerminal.Services;

/// <summary>How, if at all, this plugin can reach litd on this deployment.</summary>
public enum LitdConnectionMode
{
    /// <summary>No litd here.</summary>
    None,

    /// <summary>
    /// litd is running, but was installed by a fragment old enough that it does not mount litd's data
    /// volume into this container. There is no macaroon to be had, so nothing can be asked of it.
    /// </summary>
    Legacy,

    /// <summary>
    /// litd's data volume is mounted, so it is reached over TLS gRPC with its own macaroon.
    /// </summary>
    Headless
}

/// <param name="Mode">Whether litd is present, and whether it can be talked to.</param>
public record LitdConnectionInfo(LitdConnectionMode Mode)
{
    /// <summary>True when the plugin has everything it needs to make an authenticated call.</summary>
    public bool CanAuthenticate => Mode is LitdConnectionMode.Headless;

    /// <summary>
    /// Reading litd's log file needs the data volume mount, and there is no RPC for it.
    /// </summary>
    public bool CanReadLogs => Mode is LitdConnectionMode.Headless;
}

/// <summary>
/// Works out whether litd is present and can be talked to.
/// </summary>
/// <remarks>
/// Both signals are free and need no host access. The mount either exists in this container or it does
/// not. Failing that, the superseded fragment announces itself through <c>BTCPAY_EXTERNALSERVICES</c>,
/// which the current one no longer sets - so that entry without the mount means litd is running but
/// predates the fragment this plugin needs, and the only answer is to update btcpayserver-docker.
/// Re-evaluated per call rather than cached, because a deployment can gain or lose litd between two
/// page loads.
/// </remarks>
public class LitdConnection(LitdPaths paths, IOptions<ExternalServicesOptions> externalServices)
{
    /// <summary>
    /// The name the superseded Lightning Terminal fragment registered in <c>BTCPAY_EXTERNALSERVICES</c>
    /// for its web UI. The current fragment runs litd headless and registers nothing.
    /// </summary>
    public const string LegacyExternalServiceName = "Lightning Terminal";

    public LitdConnectionInfo Describe()
    {
        // The mount wins. It is the only signal that comes with a macaroon attached.
        if (paths.DataDirectoryMounted)
            return new LitdConnectionInfo(LitdConnectionMode.Headless);

        return new LitdConnectionInfo(
            externalServices.Value.OtherExternalServices.ContainsKey(LegacyExternalServiceName)
                ? LitdConnectionMode.Legacy
                : LitdConnectionMode.None);
    }
}
