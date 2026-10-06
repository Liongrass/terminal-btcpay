namespace BTCPayServer.Plugins.LightningTerminal.Services;

/// <summary>How, if at all, this plugin can reach litd on this deployment.</summary>
public enum LitdConnectionMode
{
    /// <summary>No litd here.</summary>
    None,

    /// <summary>
    /// litd's data directory is mounted, so it is reached over TLS gRPC with its own macaroon.
    /// </summary>
    Headless
}

/// <param name="Mode">Whether litd is present.</param>
public record LitdConnectionInfo(LitdConnectionMode Mode)
{
    /// <summary>True when the plugin has everything it needs to make an authenticated call.</summary>
    public bool CanAuthenticate => Mode is LitdConnectionMode.Headless;

    /// <summary>
    /// Reading litd's log file needs the data directory mount, and there is no RPC for it.
    /// </summary>
    public bool CanReadLogs => Mode is LitdConnectionMode.Headless;
}

/// <summary>
/// Works out whether litd is present and can be talked to.
/// </summary>
/// <remarks>
/// The signal is free and needs no host access: the data volume is either mounted into this container
/// or it is not. Re-evaluated per call rather than cached, because a deployment can gain or lose litd
/// between two page loads.
/// </remarks>
public class LitdConnection(LitdPaths paths)
{
    public LitdConnectionInfo Describe() =>
        new(paths.DataDirectoryMounted ? LitdConnectionMode.Headless : LitdConnectionMode.None);
}
