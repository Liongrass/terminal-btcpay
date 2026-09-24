using BTCPayServer.Configuration;
using BTCPayServer.Services;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.LightningTerminal.Services;

/// <summary>How, if at all, this plugin can reach litd on this deployment.</summary>
public enum LitdConnectionMode
{
    /// <summary>No litd here by either fragment.</summary>
    None,

    /// <summary>
    /// This plugin's fragment: litd's data directory is mounted, so it is reached over TLS gRPC with
    /// its own macaroon.
    /// </summary>
    Headless,

    /// <summary>
    /// btcpayserver-docker's <c>opt-add-lightning-terminal</c>: no mounted data directory and no TLS
    /// port on the network, so it is reached over the plaintext listener with the UI password.
    /// </summary>
    Upstream
}

/// <param name="Mode">Which of the two litd installs, if any, is present.</param>
/// <param name="UpstreamUrl">Where the upstream install serves its web UI, when that is the one found.</param>
/// <param name="HasUiPassword">Whether the operator has supplied the password an upstream install needs.</param>
public record LitdConnectionInfo(LitdConnectionMode Mode, Uri? UpstreamUrl, bool HasUiPassword)
{
    /// <summary>True when the plugin has everything it needs to make an authenticated call.</summary>
    public bool CanAuthenticate => Mode switch
    {
        LitdConnectionMode.Headless => true,
        LitdConnectionMode.Upstream => HasUiPassword,
        _ => false
    };

    /// <summary>
    /// True when litd is installed but the plugin is still missing the credential to talk to it. The
    /// one state that an operator can fix from this page.
    /// </summary>
    public bool NeedsUiPassword => Mode is LitdConnectionMode.Upstream && !HasUiPassword;

    /// <summary>
    /// Reading litd's log file needs the data directory mount, which only this plugin's fragment adds.
    /// There is no RPC for it, so an upstream install simply cannot offer it.
    /// </summary>
    public bool CanReadLogs => Mode is LitdConnectionMode.Headless;
}

/// <summary>
/// Works out which litd install is present and whether it can be talked to.
/// </summary>
/// <remarks>
/// Both signals are free and need no host access: the mount either exists in this container or it does
/// not, and btcpayserver-docker's own fragment announces itself through
/// <c>BTCPAY_EXTERNALSERVICES</c>. Re-evaluated per call rather than cached, because a deployment can
/// gain or lose litd between two page loads and the settings can change under a running process.
/// </remarks>
public class LitdConnection(
    LitdPaths paths,
    IOptions<ExternalServicesOptions> externalServices,
    ISettingsAccessor<LightningTerminalSettings> settings)
{
    /// <summary>
    /// The name btcpayserver-docker's Lightning Terminal fragment registers in
    /// <c>BTCPAY_EXTERNALSERVICES</c>, and the only host-free way to spot that install.
    /// </summary>
    public const string UpstreamExternalServiceName = "Lightning Terminal";

    public LitdConnectionInfo Describe()
    {
        externalServices.Value.OtherExternalServices.TryGetValue(UpstreamExternalServiceName, out var upstreamUrl);
        var hasPassword = !string.IsNullOrWhiteSpace(settings.Settings.UiPassword);

        // The mount wins. If both fragments are somehow selected, litd's data directory is present and
        // the TLS port is bound, so the better path is available and there is no reason to take the
        // plaintext one.
        if (paths.DataDirectoryMounted)
            return new LitdConnectionInfo(LitdConnectionMode.Headless, upstreamUrl, hasPassword);

        return upstreamUrl is not null
            ? new LitdConnectionInfo(LitdConnectionMode.Upstream, upstreamUrl, hasPassword)
            : new LitdConnectionInfo(LitdConnectionMode.None, null, hasPassword);
    }

    /// <summary>litd's UI password, or null when none has been supplied.</summary>
    public string? UiPassword =>
        string.IsNullOrWhiteSpace(settings.Settings.UiPassword) ? null : settings.Settings.UiPassword;
}
