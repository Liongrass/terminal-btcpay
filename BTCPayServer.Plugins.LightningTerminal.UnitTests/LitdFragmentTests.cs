using BTCPayServer.Plugins.LightningTerminal.Services;
using Xunit;
using YamlDotNet.Serialization;

namespace BTCPayServer.Plugins.LightningTerminal.UnitTests;

/// <summary>
/// Guards the Compose fragment an operator is asked to paste onto their host. A mistake here does not
/// throw at build time - it breaks someone's deployment when btcpay-setup.sh regenerates the stack.
/// </summary>
public class LitdFragmentTests
{
    private static readonly Dictionary<string, object> Fragment =
        new DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(LitdFragment.Yaml);

    private static Dictionary<object, object> Service(string name) =>
        (Dictionary<object, object>)((Dictionary<object, object>)Fragment["services"])[name];

    private static List<object> Strings(Dictionary<object, object> service, string key) =>
        (List<object>)service[key];

    [Fact]
    public void LitdRunsHeadless()
    {
        var command = Strings(Service("lnd_lit"), "command").Cast<string>().ToList();

        Assert.Contains("--disableui", command);
        // The whole reason for a custom fragment: with no UI there is no password to generate, mount
        // as a Compose secret, or protect.
        Assert.DoesNotContain(command, argument => argument.Contains("uipassword", StringComparison.OrdinalIgnoreCase));
        // No Compose secret to declare, mount or keep on the host. Asserted structurally, since the
        // fragment's header comment mentions lit_password to explain its absence.
        Assert.False(Fragment.ContainsKey("secrets"));
        Assert.False(Service("lnd_lit").ContainsKey("secrets"));
    }

    [Fact]
    public void NoPublicRoutesAreDeclared()
    {
        // Upstream's required-routes publishes /lit/ and the lnrpc./looprpc./poolrpc./litrpc. gRPC-web
        // endpoints on the public BTCPay hostname. Headless litd needs none of that reachable.
        Assert.False(Fragment.ContainsKey("required-routes"));
        Assert.False(Fragment.ContainsKey("optional-routes"));
        Assert.DoesNotContain("BTCPAY_EXTERNALSERVICES", LitdFragment.Yaml);
    }

    [Fact]
    public void LitdDataDirectoryIsMountedIntoBtcpay()
    {
        // This single mount is what lets the plugin read litd's certificate, macaroon and log without
        // any host access. Read-only, because the plugin never writes into litd's directory.
        var mounts = Strings(Service("btcpayserver"), "volumes").Cast<string>().ToList();
        Assert.Contains($"{TerminalOptions.DataVolumeName}:/lit:ro", mounts);
    }

    [Fact]
    public void LndGetsTheRpcMiddlewareLitdNeeds()
    {
        var environment = (Dictionary<object, object>)Service("lnd_bitcoin")["environment"];
        Assert.Contains("rpcmiddleware.enable=true", (string)environment["LND_EXTRA_ARGS"]);
    }

    [Fact]
    public void BundledLndIsRequired()
    {
        Assert.Contains("bitcoin-lnd", ((List<object>)Fragment["required"]).Cast<string>());

        var command = Strings(Service("lnd_lit"), "command").Cast<string>().ToList();
        Assert.Contains($"--remote.lnd.rpcserver={LightningBackendDetector.BundledLndHost}:10009", command);
    }

    [Fact]
    public void ComposeVariablesSurviveVerbatim()
    {
        // ${NBITCOIN_NETWORK} must reach the file unexpanded, so Compose resolves it from the
        // deployment's .env. The install snippet therefore has to use a *quoted* heredoc.
        Assert.Contains("--network=${NBITCOIN_NETWORK}", LitdFragment.Yaml);
        Assert.Contains("<<'LITD_FRAGMENT'", LitdFragment.InstallCommand);
    }

    [Fact]
    public void InstallSnippetWritesTheFragmentWhereBtcpayFragmentsLooksForIt()
    {
        Assert.Equal($"{TerminalOptions.FragmentName}.yml", LitdFragment.FileName);
        Assert.Equal($"{LitdFragment.FragmentDirectory}/{LitdFragment.FileName}", LitdFragment.RelativePath);
        // The snippet cds into the directory and then redirects, rather than carrying the whole path on
        // the redirect line - so assert on both halves, since neither appears as RelativePath any more.
        Assert.Contains($"cd \"$BTCPAY_BASE_DIRECTORY/{LitdFragment.FragmentDirectory}\"", LitdFragment.InstallCommand);
        Assert.Contains($"cat > {LitdFragment.FileName} <<", LitdFragment.InstallCommand);
        Assert.Contains($"btcpay-fragments add {TerminalOptions.FragmentName}", LitdFragment.InstallCommand);
        Assert.Contains(LitdFragment.Yaml, LitdFragment.InstallCommand);
    }

    [Fact]
    public void FragmentNameIsAcceptedByBtcpayFragments()
    {
        // btcpay-fragments lowercases the name, strips a .yml suffix and then requires this shape.
        Assert.Matches("^[a-z0-9][a-z0-9._-]*$", TerminalOptions.FragmentName);
        // The .custom suffix is what keeps the file across btcpay-update.sh: btcpayserver-docker
        // gitignores *.custom.yml inside its own fragment directory.
        Assert.EndsWith(".custom", TerminalOptions.FragmentName);
    }

    [Fact]
    public void UninstallKeepsTheDataVolume()
    {
        Assert.Contains($"btcpay-fragments remove {TerminalOptions.FragmentName}", LitdFragment.UninstallCommand);
        Assert.DoesNotContain("docker volume rm", LitdFragment.UninstallCommand);
        Assert.DoesNotContain("rm -f", LitdFragment.UninstallCommand);
    }

    [Fact]
    public void WipeRemovesBothFragmentsBeforeTheVolume()
    {
        // Either fragment keeps a container attached to the volume, so `docker volume rm` would fail
        // while one of them is still selected.
        var wipe = LitdFragment.WipeCommand;
        Assert.Contains(TerminalOptions.FragmentName, wipe);
        Assert.Contains(TerminalOptions.UpstreamFragmentName, wipe);
        Assert.True(
            wipe.IndexOf("btcpay-fragments remove", StringComparison.Ordinal) <
            wipe.IndexOf("docker volume rm", StringComparison.Ordinal));
    }

    [Fact]
    public void SwitchKeepsTheDataVolumeWhileReplacingTheFragment()
    {
        var @switch = LitdFragment.SwitchCommand;
        Assert.Contains($"btcpay-fragments remove {TerminalOptions.UpstreamFragmentName}", @switch);
        Assert.Contains($"btcpay-fragments add {TerminalOptions.FragmentName}", @switch);
        Assert.DoesNotContain("docker volume rm", @switch);
        // Both fragments declare the volume under the same name, which is what carries the data over.
        Assert.Contains(TerminalOptions.DataVolumeName, ((Dictionary<object, object>)Fragment["volumes"]).Keys.Cast<string>());
    }
}
