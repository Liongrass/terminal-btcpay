using BTCPayServer.Plugins.LightningTerminal.Services;
using Xunit;

namespace BTCPayServer.Plugins.LightningTerminal.UnitTests;

/// <summary>
/// The three mutually exclusive states the plugin page branches on. Getting these wrong shows the
/// wrong screen to a real operator - pitching litd to someone already running it, or offering to
/// install it twice - and no compiler check covers a Razor condition.
/// </summary>
public class LitdStatusTests
{
    private static LitdStatus StatusFor(bool installed, string? upstreamUrl) => new(
        Installed: installed,
        Running: false,
        Backend: new LightningBackend(LightningBackendKind.BundledLnd, "lnd-rest", "http://lnd_bitcoin:8080/"),
        SubServers: [],
        LndState: null,
        Error: null,
        LogAvailable: false,
        UpstreamUrl: upstreamUrl is null ? null : new Uri(upstreamUrl));

    [Fact]
    public void NothingInstalledIsTheOnlyStateThatIntroducesLitd()
    {
        var status = StatusFor(installed: false, upstreamUrl: null);

        Assert.True(status.NotFound);
        Assert.False(status.UpstreamOnly);
        Assert.False(status.Installed);
    }

    [Fact]
    public void UpstreamFragmentCountsAsLitdBeingFound()
    {
        // litd is right there serving its own web UI; the plugin just cannot see into it. Treating
        // this as "not found" would show the operator an introduction to software they already run.
        var status = StatusFor(installed: false, upstreamUrl: "https://btcpay.example.com/lit/");

        Assert.False(status.NotFound);
        Assert.True(status.UpstreamOnly);
    }

    [Fact]
    public void OurOwnFragmentIsNeverNotFound()
    {
        var status = StatusFor(installed: true, upstreamUrl: null);

        Assert.False(status.NotFound);
        Assert.False(status.UpstreamOnly);
    }

    [Fact]
    public void BothFragmentsSelectedStillCountsAsInstalled()
    {
        // Possible if someone adds the upstream fragment alongside this plugin's: the data directory
        // is mounted, so the plugin can report on litd, and it must not also offer to install it.
        var status = StatusFor(installed: true, upstreamUrl: "https://btcpay.example.com/lit/");

        Assert.False(status.NotFound);
        Assert.False(status.UpstreamOnly);
        Assert.True(status.Installed);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(false, "https://btcpay.example.com/lit/")]
    [InlineData(true, null)]
    [InlineData(true, "https://btcpay.example.com/lit/")]
    public void ExactlyOneStateIsEverTrue(bool installed, string? upstreamUrl)
    {
        var status = StatusFor(installed, upstreamUrl);

        Assert.Equal(1, new[] { status.Installed, status.UpstreamOnly, status.NotFound }.Count(state => state));
    }
}
