using BTCPayServer.Plugins.LightningTerminal.Services;
using Xunit;

namespace BTCPayServer.Plugins.LightningTerminal.UnitTests;

/// <summary>
/// The states the plugin page branches on. Getting these wrong shows the wrong screen to a real
/// operator - pitching litd to someone already running it, or offering to install it twice - and no
/// compiler check covers a Razor condition.
/// </summary>
public class LitdStatusTests
{
    private static LitdStatus StatusFor(LitdConnectionInfo connection) => new(
        Installed: connection.Mode is not LitdConnectionMode.None,
        Running: false,
        Backend: new LightningBackend(LightningBackendKind.BundledLnd, "lnd-rest", "http://lnd_bitcoin:8080/"),
        SubServers: [],
        LndState: null,
        Error: null,
        LogAvailable: false,
        Connection: connection);

    private static LitdConnectionInfo None => new(LitdConnectionMode.None);
    private static LitdConnectionInfo Legacy => new(LitdConnectionMode.Legacy);
    private static LitdConnectionInfo Headless => new(LitdConnectionMode.Headless);

    [Fact]
    public void NothingInstalledIsTheOnlyStateThatIntroducesLitd()
    {
        var status = StatusFor(None);

        Assert.True(status.NotFound);
        Assert.False(status.Installed);
    }

    [Fact]
    public void AMountedDataDirectoryCountsAsInstalled()
    {
        var status = StatusFor(Headless);

        Assert.True(status.Installed);
        Assert.False(status.NotFound);
    }

    [Fact]
    public void ALegacyInstallIsInstalledButNotReachable()
    {
        // litd is running; this plugin simply has no way in. Distinct from not installed, because the
        // page has to offer an update rather than an install, and distinct from an error, because
        // nothing was attempted.
        var status = StatusFor(Legacy);

        Assert.True(status.Installed);
        Assert.True(status.NeedsUpdate);
        Assert.False(status.NotFound);
        Assert.False(status.Running);
    }

    [Fact]
    public void OnlyALegacyInstallAsksForAnUpdate()
    {
        Assert.False(StatusFor(None).NeedsUpdate);
        Assert.False(StatusFor(Headless).NeedsUpdate);
    }

    [Theory]
    [InlineData(LitdConnectionMode.None, false)]
    [InlineData(LitdConnectionMode.Legacy, false)]
    [InlineData(LitdConnectionMode.Headless, true)]
    public void AuthenticationNeedsTheMountedMacaroon(LitdConnectionMode mode, bool canAuthenticate)
    {
        // litd's macaroon comes off the mounted data volume. Without the mount there is no credential
        // to make a call with, and no other way to obtain one.
        Assert.Equal(canAuthenticate, new LitdConnectionInfo(mode).CanAuthenticate);
    }

    [Theory]
    [InlineData(LitdConnectionMode.Headless, true)]
    [InlineData(LitdConnectionMode.Legacy, false)]
    [InlineData(LitdConnectionMode.None, false)]
    public void OnlyAMountedDataDirectoryCanYieldLogs(LitdConnectionMode mode, bool canReadLogs)
    {
        // The log is a file in litd's data directory and there is no RPC for it.
        Assert.Equal(canReadLogs, new LitdConnectionInfo(mode).CanReadLogs);
    }

    [Theory]
    [InlineData(LitdConnectionMode.None)]
    [InlineData(LitdConnectionMode.Legacy)]
    [InlineData(LitdConnectionMode.Headless)]
    public void TheScreenStatesAreMutuallyExclusive(LitdConnectionMode mode)
    {
        // NotFound introduces litd; not being true means the ordinary dashboard. Both at once would
        // render two screens on top of each other.
        var status = StatusFor(new LitdConnectionInfo(mode));

        Assert.NotEqual(status.NotFound, status.Installed);
        Assert.False(status.NotFound && status.NeedsUpdate);
        Assert.Equal(mode is LitdConnectionMode.None, status.NotFound);
        Assert.Equal(mode is LitdConnectionMode.Legacy, status.NeedsUpdate);
    }
}
