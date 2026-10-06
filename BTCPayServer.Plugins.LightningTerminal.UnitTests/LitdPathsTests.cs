using BTCPayServer.Plugins.LightningTerminal.Services;
using Xunit;

namespace BTCPayServer.Plugins.LightningTerminal.UnitTests;

/// <summary>
/// These paths decide whether the plugin reports litd as installed, and whether it can authenticate to
/// it, so the interesting cases are the partial ones: mounted but empty, and a network directory
/// spelled differently than BTCPay spells it.
/// </summary>
public class LitdPathsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("litd-paths-tests").FullName;

    /// <summary>litd's own directory inside the shared volume - what the fragment's --lit-dir points at.</summary>
    private string LitDir => Path.Combine(_root, TerminalOptions.LitdSubdirectory);

    private LitdPaths PathsFor(string network) =>
        new(new TerminalOptions(_root, "lnd_lit", 8443, network));

    private void Write(string relativePath, string content = "x")
    {
        var full = Path.Combine(LitDir, relativePath);
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

        Assert.Equal(Path.Combine(LitDir, "tls.cert"), paths.TlsCertificateFile);
        Assert.Equal(Path.Combine(LitDir, "mainnet", "lit.macaroon"), paths.MacaroonFile);
        Assert.Equal(Path.Combine(LitDir, "logs", "mainnet", "litd.log"), paths.LogFile);
    }

    [Fact]
    public void FallsBackToWhicheverNetworkDirectoryExists()
    {
        // BTCPay and litd do not always agree on a network's name - testnet4 is the live example.
        // Reporting nothing would be worse than using the one directory litd actually created.
        Write(Path.Combine("testnet4", "lit.macaroon"));
        Write(Path.Combine("logs", "testnet4", "litd.log"));

        var paths = PathsFor("testnet");

        Assert.Equal(Path.Combine(LitDir, "testnet4", "lit.macaroon"), paths.MacaroonFile);
        Assert.Equal(Path.Combine(LitDir, "logs", "testnet4", "litd.log"), paths.LogFile);
    }

    [Fact]
    public void PrefersTheConfiguredNetworkOverAnotherOnDisk()
    {
        // A node moved from testnet leaves its old directory behind; the configured network wins.
        Write(Path.Combine("mainnet", "lit.macaroon"));
        Write(Path.Combine("testnet", "lit.macaroon"));

        Assert.Equal(Path.Combine(LitDir, "mainnet", "lit.macaroon"), PathsFor("mainnet").MacaroonFile);
        Assert.Equal(Path.Combine(LitDir, "testnet", "lit.macaroon"), PathsFor("testnet").MacaroonFile);
    }

    [Fact]
    public void ResolvesAgainAfterLitdStarts()
    {
        // Resolution must not be cached at construction: BTCPay frequently starts before litd has
        // written any of this, and the plugin has to notice when it does.
        var paths = PathsFor("mainnet");
        Assert.Null(paths.MacaroonFile);

        Write(Path.Combine("mainnet", "lit.macaroon"));

        Assert.Equal(Path.Combine(LitDir, "mainnet", "lit.macaroon"), paths.MacaroonFile);
    }

    [Fact]
    public void LitdsOwnDirectoryIsNestedInsideTheSharedVolume()
    {
        // The volume carries every litd-family daemon's directory, so litd's own files sit one level
        // down. The volume answers "installed"; .lit appearing inside it answers "started".
        var options = new TerminalOptions(_root, "lnd_lit", 8443, "mainnet");

        Assert.Equal(_root, options.LitVolumeDirectory);
        Assert.Equal(Path.Combine(_root, ".lit"), options.LitDataDirectory);
        Assert.True(new LitdPaths(options).DataDirectoryMounted);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
