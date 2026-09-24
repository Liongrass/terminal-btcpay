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
    private static readonly Uri UpstreamUi = new("https://btcpay.example.com/lit/");

    private static LitdStatus StatusFor(LitdConnectionInfo connection) => new(
        Installed: connection.Mode is not LitdConnectionMode.None,
        Running: false,
        Backend: new LightningBackend(LightningBackendKind.BundledLnd, "lnd-rest", "http://lnd_bitcoin:8080/"),
        SubServers: [],
        LndState: null,
        Error: null,
        LogAvailable: false,
        UpstreamUrl: connection.UpstreamUrl,
        Connection: connection);

    private static LitdConnectionInfo None => new(LitdConnectionMode.None, null, false);
    private static LitdConnectionInfo Headless => new(LitdConnectionMode.Headless, null, false);
    private static LitdConnectionInfo UpstreamNoPassword => new(LitdConnectionMode.Upstream, UpstreamUi, false);
    private static LitdConnectionInfo UpstreamWithPassword => new(LitdConnectionMode.Upstream, UpstreamUi, true);

    [Fact]
    public void NothingInstalledIsTheOnlyStateThatIntroducesLitd()
    {
        var status = StatusFor(None);

        Assert.True(status.NotFound);
        Assert.False(status.Installed);
        Assert.False(status.UpstreamOnly);
        Assert.False(status.NeedsUiPassword);
    }

    [Fact]
    public void AnUpstreamInstallCountsAsInstalled()
    {
        // It used to be a dead end. Now it is reachable over the plaintext listener, so the page shows
        // the same sessions and accounts once a password is stored.
        var status = StatusFor(UpstreamWithPassword);

        Assert.True(status.Installed);
        Assert.True(status.UpstreamOnly);
        Assert.False(status.NotFound);
        Assert.False(status.NeedsUiPassword);
    }

    [Fact]
    public void AnUpstreamInstallWithoutAPasswordAsksForOne()
    {
        // Distinct from litd being down: litd is fine, the plugin just has no credential for it, and
        // that is the one state an operator can fix from this page.
        var status = StatusFor(UpstreamNoPassword);

        Assert.True(status.Installed);
        Assert.True(status.NeedsUiPassword);
        Assert.False(status.NotFound);
    }

    [Fact]
    public void OurOwnFragmentNeverAsksForAPassword()
    {
        // The headless fragment mounts litd's macaroon, so there is nothing to type in.
        var status = StatusFor(Headless);

        Assert.True(status.Installed);
        Assert.False(status.NeedsUiPassword);
        Assert.False(status.UpstreamOnly);
        Assert.False(status.NotFound);
    }

    [Theory]
    [InlineData(LitdConnectionMode.None, false, false)]
    [InlineData(LitdConnectionMode.Headless, false, true)]
    [InlineData(LitdConnectionMode.Upstream, false, false)]
    [InlineData(LitdConnectionMode.Upstream, true, true)]
    public void AuthenticationNeedsAMacaroonOrAPassword(
        LitdConnectionMode mode, bool hasPassword, bool canAuthenticate)
    {
        var connection = new LitdConnectionInfo(mode, null, hasPassword);

        Assert.Equal(canAuthenticate, connection.CanAuthenticate);
    }

    [Theory]
    [InlineData(LitdConnectionMode.Headless, true)]
    [InlineData(LitdConnectionMode.Upstream, false)]
    [InlineData(LitdConnectionMode.None, false)]
    public void OnlyAMountedDataDirectoryCanYieldLogs(LitdConnectionMode mode, bool canReadLogs)
    {
        // The log is a file in litd's data directory and there is no RPC for it, so an upstream
        // install cannot offer the download at all.
        Assert.Equal(canReadLogs, new LitdConnectionInfo(mode, null, true).CanReadLogs);
    }

    [Theory]
    [InlineData(LitdConnectionMode.None)]
    [InlineData(LitdConnectionMode.Headless)]
    [InlineData(LitdConnectionMode.Upstream)]
    public void TheScreenStatesAreMutuallyExclusive(LitdConnectionMode mode)
    {
        // NotFound introduces litd, UpstreamOnly explains BTCPay's install, and neither being true
        // means the ordinary dashboard. Two at once would render two screens on top of each other.
        var status = StatusFor(
            new LitdConnectionInfo(mode, mode is LitdConnectionMode.Upstream ? UpstreamUi : null, true));

        Assert.False(status.NotFound && status.UpstreamOnly);
        Assert.Equal(mode is LitdConnectionMode.None, status.NotFound);
        Assert.Equal(mode is LitdConnectionMode.Upstream, status.UpstreamOnly);
    }
}
