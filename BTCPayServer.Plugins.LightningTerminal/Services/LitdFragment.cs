namespace BTCPayServer.Plugins.LightningTerminal.Services;

/// <summary>
/// Builds the Docker Compose fragment that adds litd to a btcpayserver-docker deployment, and the
/// host commands an operator runs to add, remove or wipe it.
/// </summary>
/// <remarks>
/// <para>
/// The commands are meant to be pasted into a root shell on the host. BTCPay Server cannot run them
/// itself: since 2.4.4 a plugin's only channel to the host is <c>btcpay-host</c>, whose host-side
/// script accepts a fixed whitelist (<c>env help changedomain update clean restart</c>) and has no
/// fragment command. Everything else this plugin does - status, sessions, logs - needs no host
/// access at all, so this is the one step an operator performs by hand.
/// </para>
/// <para>
/// The generated file is a <c>.custom.yml</c> fragment, which btcpayserver-docker gitignores inside
/// its own fragment directory; that is what keeps it in place across <c>btcpay-update.sh</c>.
/// </para>
/// </remarks>
public class LitdFragment(TerminalOptions options)
{
    /// <summary>
    /// Whether Lightning Labs runs an Autopilot server for <paramref name="network"/>.
    /// </summary>
    /// <remarks>
    /// Mirrors litd's own switch in terminal.go, which fills the Autopilot address in from
    /// <c>--network</c> for mainnet and testnet and, for anything else, returns "no autopilot server
    /// address specified". That aborts startup - litd never comes up at all, rather than coming up with
    /// one sub-server degraded - so the client has to be switched off explicitly.
    /// <para>
    /// btcpay-setup.sh refuses any NBITCOIN_NETWORK outside mainnet, testnet and regtest, so in a
    /// btcpayserver-docker deployment this only ever bites on regtest. The rule is written as litd's
    /// rather than as a regtest special case so it stays correct if BTCPay ever allows signet, which
    /// litd would reject the same way.
    /// </para>
    /// </remarks>
    internal static bool AutopilotAvailableOn(string network) => network is "mainnet" or "testnet";

    /// <summary>litd's arguments, already indented as a YAML sequence under <c>command:</c>.</summary>
    private string CommandArguments => string.Join("\n", Arguments.Select(argument => $"      - \"{argument}\""));

    private IEnumerable<string> Arguments
    {
        get
        {
            yield return "--disableui";
            yield return $"--httpslisten=0.0.0.0:{TerminalOptions.DefaultRpcPort}";
            yield return "--network=${NBITCOIN_NETWORK}";
            yield return "--lnd-mode=remote";
            yield return $"--remote.lnd.rpcserver={LightningBackendDetector.BundledLndHost}:10009";
            yield return "--remote.lnd.macaroonpath=/data/lnd/admin.macaroon";
            yield return "--remote.lnd.tlscertpath=/data/lnd/tls.cert";

            if (!AutopilotAvailableOn(options.Network))
                yield return "--autopilot.disable";
        }
    }

    /// <summary>
    /// litd release run by the generated fragment.
    /// </summary>
    /// <remarks>
    /// btcpayserver-docker pins the <c>-path-prefix</c> rebuild of the same release, which exists only
    /// to serve the web UI's assets under <c>/lit/</c>. With <c>--disableui</c> there are no assets to
    /// serve, so the plain upstream Lightning Labs image is the right one here.
    /// Bump in step with btcpayserver-docker's own pin; see README.md.
    /// </remarks>
    public const string Image = "lightninglabs/lightning-terminal:v0.17.5-alpha";

    /// <summary>btcpayserver-docker's fragment directory, relative to the deployment's base directory.</summary>
    public const string FragmentDirectory = "btcpayserver-docker/docker-compose-generator/docker-fragments";

    /// <summary>File name of the generated fragment. btcpay-fragments takes the name without the suffix.</summary>
    public const string FileName = $"{TerminalOptions.FragmentName}.yml";

    /// <summary>Path of the generated fragment on the host, relative to the deployment's base directory.</summary>
    public const string RelativePath = $"{FragmentDirectory}/{FileName}";

    /// <summary>
    /// The fragment's YAML. <c>${NBITCOIN_NETWORK}</c> is left for Compose to expand at run time,
    /// exactly as the upstream fragment leaves it, so the install snippet must quote its heredoc.
    /// </summary>
    /// <remarks>
    /// Trimmed relative to btcpayserver-docker's fragment, beyond turning the UI off:
    /// <list type="bullet">
    /// <item><description>
    /// <c>--httpslisten</c> is set rather than <c>--insecure-httplisten</c>. Upstream needs the plaintext
    /// listener because nginx proxies <c>/lit/</c> to it; this plugin speaks gRPC over TLS instead, so the
    /// plaintext port is pure attack surface. It has to be set explicitly all the same: litd defaults
    /// <c>--httpslisten</c> to <c>127.0.0.1:8443</c>, which no sibling container can reach.
    /// </description></item>
    /// <item><description>
    /// No <c>LIT_AUTO_MIGRATE_TO_SQL</c>. litd has defaulted to SQLite since v0.17 and only prompts when
    /// it finds legacy kvdb files, so a fresh install never sees the prompt this suppresses.
    /// </description></item>
    /// <item><description>
    /// No Faraday bitcoind wiring, and so no <c>bitcoin_datadir</c> mount or <c>bitcoind</c> link.
    /// Faraday's <c>connect_bitcoin</c> defaults off; leaving it off costs the handful of Faraday
    /// endpoints that need chain data and removes litd's reach into Bitcoin Core's data directory.
    /// </description></item>
    /// </list>
    /// </remarks>
    public string Yaml =>
        $$"""
        # Generated by the Lightning Terminal plugin

        services:
          btcpayserver:
            volumes:
              - "{{TerminalOptions.DataVolumeName}}:/lit:ro"
          {{LightningBackendDetector.BundledLndHost}}:
            environment:
              LND_EXTRA_ARGS: |
                rpcmiddleware.enable=true
          {{TerminalOptions.DefaultRpcHost}}:
            image: "{{Image}}"
            container_name: {{TerminalOptions.ContainerName}}
            restart: unless-stopped
            expose:
              - "{{TerminalOptions.DefaultRpcPort}}"
            volumes:
              - "{{TerminalOptions.DataVolumeName}}:/root/.lit"
              - "lnd_bitcoin_datadir:/data/lnd:ro"
            links:
              - {{LightningBackendDetector.BundledLndHost}}
            command:
        {{CommandArguments}}

        volumes:
          {{TerminalOptions.DataVolumeName}}:

        required:
          - "bitcoin-lnd"
        """;

    /// <summary>
    /// Writes the fragment and selects it. <c>btcpay-fragments add</c> regenerates the Compose file and
    /// brings the stack back up immediately, so BTCPay Server itself restarts partway through.
    /// </summary>
    /// <remarks>
    /// Refuses outright if btcpayserver-docker's own Lightning Terminal fragment is still selected.
    /// Both declare an image for <c>lnd_lit</c>, and the generator merges services with
    /// <c>SingleOrDefault(n =&gt; n.Children.ContainsKey("image"))</c> - two of them throws, so
    /// btcpay-setup.sh dies with an unhandled .NET stack trace rather than saying anything useful.
    /// The check is read-only and costs nothing on a deployment that has no upstream fragment; it
    /// stops before writing anything, so a refused run leaves no stray file behind. Removing a running
    /// litd is the operator's call, not a side effect of a paste labelled "install" - the page offers
    /// Switch for that, which does both halves deliberately.
    /// </remarks>
    /// <remarks>
    /// Every command here runs in a <c>( set -eu )</c> subshell. These are pasted into a root shell, so
    /// an unset <c>BTCPAY_BASE_DIRECTORY</c> - on a host that is not a btcpayserver-docker deployment -
    /// has to stop the block rather than resolve to a path under <c>/</c>. A subshell also means a
    /// failure never closes the operator's session the way a bare <c>exit</c> would.
    /// </remarks>
    public string InstallCommand =>
        $$"""
        (
        set -eu
        . /etc/profile.d/btcpay-env.sh
        if btcpay-fragments show | jq -e --arg f {{TerminalOptions.UpstreamFragmentName}} '.additionalFragments | index($f) != null' > /dev/null; then
            echo "BTCPay Server's own Lightning Terminal fragment ({{TerminalOptions.UpstreamFragmentName}}) is already selected." >&2
            echo "Two fragments cannot both define the lnd_lit container - the compose generator fails outright." >&2
            echo "Remove it first, then run this again:" >&2
            echo "    btcpay-fragments remove {{TerminalOptions.UpstreamFragmentName}}" >&2
            exit 1
        fi
        cd "$BTCPAY_BASE_DIRECTORY/{{FragmentDirectory}}"
        cat > {{FileName}} <<'LITD_FRAGMENT'
        {{Yaml}}
        LITD_FRAGMENT
        btcpay-fragments add {{TerminalOptions.FragmentName}}
        )
        """;

    /// <summary>
    /// Replaces btcpayserver-docker's own Lightning Terminal fragment with this plugin's headless one,
    /// keeping litd's data volume and so the node's existing accounts, sessions and history.
    /// </summary>
    /// <remarks>
    /// Two <c>btcpay-fragments</c> calls, because each one regenerates and restarts the stack the moment
    /// it runs and there is no combined remove-and-add. litd is therefore down between them.
    /// </remarks>
    public string SwitchCommand =>
        $"""
        (
        set -eu
        . /etc/profile.d/btcpay-env.sh
        cd "$BTCPAY_BASE_DIRECTORY/{FragmentDirectory}"
        cat > {FileName} <<'LITD_FRAGMENT'
        {Yaml}
        LITD_FRAGMENT
        btcpay-fragments remove {TerminalOptions.UpstreamFragmentName}
        btcpay-fragments add {TerminalOptions.FragmentName}
        )
        """;

    /// <summary>
    /// Deselects the fragment and regenerates the stack without litd. litd's data volume is
    /// untouched: nothing here removes a named Compose volume, so the macaroon, accounts, sessions
    /// and Loop/Pool/Faraday records all survive, and a later re-install picks them straight back up.
    /// </summary>
    public string UninstallCommand =>
        $"""
        (
        set -eu
        . /etc/profile.d/btcpay-env.sh
        btcpay-fragments remove {TerminalOptions.FragmentName}
        )
        """;

    /// <summary>
    /// Removes litd and then destroys its data volume. Irreversible: the macaroon, every LNC session,
    /// every Loop/Pool/Faraday record and litd's own database go with it. LND's own data is in a
    /// separate volume and is not touched.
    /// </summary>
    /// <remarks>
    /// Both the generated fragment and btcpayserver-docker's own are deselected, because either of them
    /// keeps a container attached to the volume, and each is removed only if it is actually selected -
    /// <c>btcpay-fragments</c> rebuilds and restarts the whole stack on every call, selected or not.
    /// </remarks>
    public string WipeCommand =>
        $$"""
        (
        set -eu
        . /etc/profile.d/btcpay-env.sh
        for fragment in {{TerminalOptions.FragmentName}} {{TerminalOptions.UpstreamFragmentName}}; do
            if btcpay-fragments show | jq -e --arg f "$fragment" '.additionalFragments | index($f) != null' > /dev/null; then
                btcpay-fragments remove "$fragment"
            fi
        done
        rm -f "$BTCPAY_BASE_DIRECTORY/{{RelativePath}}"
        volume="$(docker volume ls -q | grep -E '(^|_){{TerminalOptions.DataVolumeName}}$' || true)"
        if [ -n "$volume" ]; then
            docker volume rm "$volume"
        else
            echo "No {{TerminalOptions.DataVolumeName}} volume found - nothing left to wipe."
        fi
        )
        """;
}
