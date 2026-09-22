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
    /// <summary>A fragment as generated for a mainnet deployment, which is the default shape.</summary>
    private static readonly LitdFragment Mainnet = FragmentFor("mainnet");

    private static readonly Dictionary<string, object> Fragment = Parse(Mainnet.Yaml);

    internal static LitdFragment FragmentFor(string network) =>
        new(new TerminalOptions(
            "/lit", TerminalOptions.DefaultRpcHost, TerminalOptions.DefaultRpcPort, network));

    private static Dictionary<string, object> Parse(string yaml) =>
        new DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(yaml);

    internal static List<string> ArgumentsFor(string network) =>
        ((List<object>)((Dictionary<object, object>)((Dictionary<object, object>)
            Parse(FragmentFor(network).Yaml)["services"])[TerminalOptions.DefaultRpcHost])["command"])
        .Cast<string>().ToList();

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
    public void LitdListensWhereThePluginDials()
    {
        // The invariant that makes the whole plugin work, and the one that is silently broken by
        // omission: litd defaults --httpslisten to 127.0.0.1:8443, which is loopback inside its own
        // container and unreachable from the BTCPay container. Nothing fails at build time; litd just
        // never answers. Bound to the service name and port LitdClient actually dials.
        var service = Service(TerminalOptions.DefaultRpcHost);
        var command = Strings(service, "command").Cast<string>().ToList();

        Assert.Contains($"--httpslisten=0.0.0.0:{TerminalOptions.DefaultRpcPort}", command);
        Assert.Contains($"{TerminalOptions.DefaultRpcPort}", Strings(service, "expose").Select(port => port.ToString()));
    }

    [Theory]
    [InlineData("regtest")]
    public void AutopilotIsDisabledWhereLightningLabsRunsNoAutopilotServer(string network)
    {
        // litd resolves the Autopilot address from --network and, for anything but mainnet/testnet,
        // returns "no autopilot server address specified" - which aborts startup. litd does not come up
        // at all, so this is the difference between a working regtest deployment and a dead one.
        Assert.Contains("--autopilot.disable", ArgumentsFor(network));
    }

    [Theory]
    [InlineData("mainnet")]
    [InlineData("testnet")]
    public void AutopilotIsLeftOnWhereItWorks(string network)
    {
        // Disabling it everywhere would be the safe-looking choice and would quietly cost mainnet
        // operators the Autopilot sessions Terminal offers.
        Assert.DoesNotContain("--autopilot.disable", ArgumentsFor(network));
    }

    [Theory]
    [InlineData("mainnet", true)]
    [InlineData("testnet", true)]
    [InlineData("regtest", false)]
    [InlineData("simnet", false)]
    [InlineData("signet", false)]
    [InlineData("testnet4", false)]
    public void AutopilotAvailabilityMirrorsLitdsOwnSwitch(string network, bool available)
    {
        // btcpay-setup.sh only accepts mainnet, testnet and regtest, so signet, testnet4 and simnet
        // cannot reach us through btcpayserver-docker today. The rule is still written as litd's own,
        // so it stays right if BTCPay ever widens that list - litd would reject those the same way.
        Assert.Equal(available, LitdFragment.AutopilotAvailableOn(network));
    }

    [Fact]
    public void ContainerIsNamedTheWayBtcpayNamesItsDaemons()
    {
        // btcpayserver_ plus the daemon's own name, as in btcpayserver_bitcoind and
        // btcpayserver_lnd_bitcoin. Without container_name, Compose derives generated-lnd_lit-1, which
        // is what an operator would otherwise find in docker ps.
        Assert.Equal(TerminalOptions.ContainerName, Service(TerminalOptions.DefaultRpcHost)["container_name"]);
        Assert.StartsWith("btcpayserver_", TerminalOptions.ContainerName);
    }

    [Fact]
    public void TheServiceNameIsNotRenamedAlongWithTheContainer()
    {
        // The service name is litd's DNS name on the Docker network and is what upstream's fragment
        // calls it too. Renaming it would move the address LitdClient dials and split this fragment
        // from upstream's for anyone switching between the two, so only the container name changed.
        Assert.Equal("lnd_lit", TerminalOptions.DefaultRpcHost);
        Assert.True(((Dictionary<object, object>)Fragment["services"]).ContainsKey(TerminalOptions.DefaultRpcHost));
        Assert.StartsWith(TerminalOptions.DefaultRpcHost, TerminalOptions.DataVolumeName);
    }

    [Fact]
    public void NoPlaintextListenerIsOpened()
    {
        // Upstream opens one because nginx proxies /lit/ to it. This plugin speaks TLS gRPC instead,
        // so a plaintext port carrying macaroons would be pure attack surface.
        var command = Strings(Service(TerminalOptions.DefaultRpcHost), "command").Cast<string>().ToList();

        Assert.DoesNotContain(command, argument => argument.Contains("insecure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NothingReachesIntoBitcoinCoresDataDirectory()
    {
        // Only Faraday's optional bitcoind connection ever needed it, and that defaults off.
        var service = Service(TerminalOptions.DefaultRpcHost);

        Assert.DoesNotContain(Strings(service, "volumes").Cast<string>(), mount => mount.StartsWith("bitcoin_datadir:"));
        Assert.DoesNotContain("bitcoind", Strings(service, "links").Cast<string>());
        Assert.DoesNotContain("faraday", Mainnet.Yaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoSqlMigrationOverrideIsForced()
    {
        // litd has defaulted to SQLite since v0.17 and only prompts when it finds legacy kvdb files,
        // so a fresh install never sees the prompt this used to suppress.
        Assert.DoesNotContain("LIT_AUTO_MIGRATE_TO_SQL", Mainnet.Yaml);
    }

    [Fact]
    public void OnlyTheGeneratedByLineSurvivesAsAComment()
    {
        var comments = Mainnet.Yaml
            .Split('\n')
            .Where(line => line.TrimStart().StartsWith('#'))
            .ToArray();

        Assert.Equal(["# Generated by the Lightning Terminal plugin"], comments);
    }

    [Fact]
    public void NoPublicRoutesAreDeclared()
    {
        // Upstream's required-routes publishes /lit/ and the lnrpc./looprpc./poolrpc./litrpc. gRPC-web
        // endpoints on the public BTCPay hostname. Headless litd needs none of that reachable.
        Assert.False(Fragment.ContainsKey("required-routes"));
        Assert.False(Fragment.ContainsKey("optional-routes"));
        Assert.DoesNotContain("BTCPAY_EXTERNALSERVICES", Mainnet.Yaml);
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
        Assert.Contains("--network=${NBITCOIN_NETWORK}", Mainnet.Yaml);
        Assert.Contains("<<'LITD_FRAGMENT'", Mainnet.InstallCommand);
    }

    [Fact]
    public void InstallSnippetWritesTheFragmentWhereBtcpayFragmentsLooksForIt()
    {
        Assert.Equal($"{TerminalOptions.FragmentName}.yml", LitdFragment.FileName);
        Assert.Equal($"{LitdFragment.FragmentDirectory}/{LitdFragment.FileName}", LitdFragment.RelativePath);
        // The snippet cds into the directory and then redirects, rather than carrying the whole path on
        // the redirect line - so assert on both halves, since neither appears as RelativePath any more.
        Assert.Contains($"cd \"$BTCPAY_BASE_DIRECTORY/{LitdFragment.FragmentDirectory}\"", Mainnet.InstallCommand);
        Assert.Contains($"cat > {LitdFragment.FileName} <<", Mainnet.InstallCommand);
        Assert.Contains($"btcpay-fragments add {TerminalOptions.FragmentName}", Mainnet.InstallCommand);
        Assert.Contains(Mainnet.Yaml, Mainnet.InstallCommand);
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
        Assert.Contains($"btcpay-fragments remove {TerminalOptions.FragmentName}", Mainnet.UninstallCommand);
        Assert.DoesNotContain("docker volume rm", Mainnet.UninstallCommand);
        Assert.DoesNotContain("rm -f", Mainnet.UninstallCommand);
    }

    [Fact]
    public void WipeRemovesBothFragmentsBeforeTheVolume()
    {
        // Either fragment keeps a container attached to the volume, so `docker volume rm` would fail
        // while one of them is still selected.
        var wipe = Mainnet.WipeCommand;
        Assert.Contains(TerminalOptions.FragmentName, wipe);
        Assert.Contains(TerminalOptions.UpstreamFragmentName, wipe);
        Assert.True(
            wipe.IndexOf("btcpay-fragments remove", StringComparison.Ordinal) <
            wipe.IndexOf("docker volume rm", StringComparison.Ordinal));
    }

    [Fact]
    public void SwitchKeepsTheDataVolumeWhileReplacingTheFragment()
    {
        var @switch = Mainnet.SwitchCommand;
        Assert.Contains($"btcpay-fragments remove {TerminalOptions.UpstreamFragmentName}", @switch);
        Assert.Contains($"btcpay-fragments add {TerminalOptions.FragmentName}", @switch);
        Assert.DoesNotContain("docker volume rm", @switch);
        // Both fragments declare the volume under the same name, which is what carries the data over.
        Assert.Contains(TerminalOptions.DataVolumeName, ((Dictionary<object, object>)Fragment["volumes"]).Keys.Cast<string>());
    }
}
