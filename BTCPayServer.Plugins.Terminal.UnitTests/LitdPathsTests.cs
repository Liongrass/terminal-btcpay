using BTCPayServer.Plugins.Terminal.Services;
using Xunit;

namespace BTCPayServer.Plugins.Terminal.UnitTests;

/// <summary>
/// These paths decide whether the plugin reports litd as installed, and whether it can authenticate to
/// it, so the interesting cases are the partial ones: mounted but empty, and a network directory
/// spelled differently than BTCPay spells it.
/// </summary>
public class LitdPathsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("litd-paths-tests").FullName;

    private LitdPaths PathsFor(string network) =>
        new(new TerminalOptions(_root, "lnd_lit", 8443, network));

    private void Write(string relativePath, string content = "x")
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public void MissingDataDirectoryMeansNotInstalled()
    {
        var paths = new LitdPaths(new TerminalOptions(
            Path.Combine(_root, "definitely-not-mounted"), "lnd_lit", 8443, "mainnet"));

        Assert.False(paths.DataDirectoryMounted);
        Assert.Null(paths.TlsCertificateFile);
        Assert.Null(paths.MacaroonFile);
        Assert.Null(paths.LogFile);
    }

    [Fact]
    public void MountedButEmptyMeansInstalledAndNotYetStarted()
    {
        // litd creates all of these on its first run, so this is what a freshly installed, still
        // starting deployment looks like - installed, but nothing to connect with yet.
        var paths = PathsFor("mainnet");

        Assert.True(paths.DataDirectoryMounted);
        Assert.Null(paths.TlsCertificateFile);
        Assert.Null(paths.MacaroonFile);
        Assert.Null(paths.LogFile);
    }

    [Fact]
    public void FindsCertificateMacaroonAndLogOnTheConfiguredNetwork()
    {
        Write("tls.cert");
        Write(Path.Combine("mainnet", "lit.macaroon"));
        Write(Path.Combine("logs", "mainnet", "litd.log"));

        var paths = PathsFor("mainnet");

        Assert.Equal(Path.Combine(_root, "tls.cert"), paths.TlsCertificateFile);
        Assert.Equal(Path.Combine(_root, "mainnet", "lit.macaroon"), paths.MacaroonFile);
        Assert.Equal(Path.Combine(_root, "logs", "mainnet", "litd.log"), paths.LogFile);
    }

    [Fact]
    public void FallsBackToWhicheverNetworkDirectoryExists()
    {
        // BTCPay and litd do not always agree on a network's name - testnet4 is the live example.
        // Reporting nothing would be worse than using the one directory litd actually created.
        Write(Path.Combine("testnet4", "lit.macaroon"));
        Write(Path.Combine("logs", "testnet4", "litd.log"));

        var paths = PathsFor("testnet");

        Assert.Equal(Path.Combine(_root, "testnet4", "lit.macaroon"), paths.MacaroonFile);
        Assert.Equal(Path.Combine(_root, "logs", "testnet4", "litd.log"), paths.LogFile);
    }

    [Fact]
    public void PrefersTheConfiguredNetworkOverAnotherOnDisk()
    {
        // A node moved from testnet leaves its old directory behind; the configured network wins.
        Write(Path.Combine("mainnet", "lit.macaroon"));
        Write(Path.Combine("testnet", "lit.macaroon"));

        Assert.Equal(Path.Combine(_root, "mainnet", "lit.macaroon"), PathsFor("mainnet").MacaroonFile);
        Assert.Equal(Path.Combine(_root, "testnet", "lit.macaroon"), PathsFor("testnet").MacaroonFile);
    }

    [Fact]
    public void ResolvesAgainAfterLitdStarts()
    {
        // Resolution must not be cached at construction: BTCPay frequently starts before litd has
        // written any of this, and the plugin has to notice when it does.
        var paths = PathsFor("mainnet");
        Assert.Null(paths.MacaroonFile);

        Write(Path.Combine("mainnet", "lit.macaroon"));

        Assert.Equal(Path.Combine(_root, "mainnet", "lit.macaroon"), paths.MacaroonFile);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
