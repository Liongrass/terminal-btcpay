using BTCPayServer.Configuration;
using Microsoft.Extensions.Configuration;

namespace BTCPayServer.Plugins.LightningTerminal.Services;

/// <summary>
/// Where this plugin expects to find litd, and what it calls the pieces it asks the operator to
/// install on the host.
/// </summary>
/// <remarks>
/// Every path here is inside the BTCPay Server container, and every one of them only exists
/// because the fragment this plugin generates (see <see cref="LitdFragment"/>) mounts litd's data
/// directory into the BTCPay container. That mount is the whole reason the plugin can report
/// status, mint LNC sessions and read logs without any host shell access - see README.md.
/// </remarks>
public class TerminalOptions
{
    public TerminalOptions(IConfiguration configuration, BTCPayServerOptions serverOptions)
        : this(
            configuration["terminallitdatadir"] ?? "/lit",
            configuration["terminallitrpchost"] ?? DefaultRpcHost,
            int.TryParse(configuration["terminallitrpcport"], out var port) ? port : DefaultRpcPort,
            configuration["terminallitnetwork"] ?? serverOptions.NetworkType.ToString())
    {
        LndDataDirectory = configuration["terminallnddatadir"] ?? DefaultLndDataDirectory;
        LndRpcHost = configuration["terminallndrpchost"] ?? LightningBackendDetector.BundledLndHost;
        LndRpcPort = int.TryParse(configuration["terminallndrpcport"], out var lndPort) ? lndPort : DefaultLndRpcPort;
    }

    internal TerminalOptions(string litDataDirectory, string rpcHost, int rpcPort, string network)
    {
        LitDataDirectory = litDataDirectory;
        RpcHost = rpcHost;
        RpcPort = rpcPort;
        Network = network.ToLowerInvariant();
        LndDataDirectory = DefaultLndDataDirectory;
        LndRpcHost = LightningBackendDetector.BundledLndHost;
        LndRpcPort = DefaultLndRpcPort;
    }

    /// <summary>
    /// Where btcpayserver-docker mounts the bundled LND's data directory into the BTCPay container.
    /// Not this plugin's doing - bitcoin-lnd.yml puts it there so BTCPay can drive the internal node,
    /// and BTCPAY_BTCLIGHTNING points at the same path.
    /// </summary>
    public const string DefaultLndDataDirectory = "/etc/lnd_bitcoin";

    /// <summary>LND's gRPC port. bitcoin-lnd.yml passes rpclisten=lnd_bitcoin:10009.</summary>
    public const int DefaultLndRpcPort = 10009;

    /// <summary>The bundled LND's data directory, holding its admin macaroon and TLS certificate.</summary>
    public string LndDataDirectory { get; }

    public string LndRpcHost { get; }

    public int LndRpcPort { get; }

    /// <summary>LND's admin macaroon, which is what lets this plugin bake account macaroons.</summary>
    public string LndMacaroonFile => Path.Combine(LndDataDirectory, "admin.macaroon");

    /// <summary>LND's self-signed TLS certificate.</summary>
    public string LndTlsCertificateFile => Path.Combine(LndDataDirectory, "tls.cert");

    /// <summary>litd's <c>--lit-dir</c> (<c>lnd_lit_datadir</c>) as mounted into this container.</summary>
    public string LitDataDirectory { get; }

    /// <summary>
    /// Compose service name of the litd container, which doubles as its DNS name. The generated
    /// fragment declares the service under this exact name - see <see cref="LitdFragment"/>.
    /// </summary>
    public const string DefaultRpcHost = "lnd_lit";

    /// <summary>
    /// litd's <c>--httpslisten</c> port, which the generated fragment sets explicitly.
    /// </summary>
    /// <remarks>
    /// litd defaults this to <c>127.0.0.1:8443</c> - loopback inside its own container, which a
    /// sibling container cannot reach at all. The fragment has to bind it to <c>0.0.0.0</c> on this
    /// port or nothing here can talk to litd.
    /// </remarks>
    public const int DefaultRpcPort = 8443;

    /// <summary>
    /// litd's <c>--insecure-httplisten</c> port, which btcpayserver-docker's own fragment binds to the
    /// Docker network so nginx can proxy the web UI to it.
    /// </summary>
    /// <remarks>
    /// Only used for an upstream install. This plugin's own fragment opens no plaintext port at all.
    /// </remarks>
    public const int DefaultUpstreamHttpPort = 8080;

    /// <summary>litd's plaintext port on an upstream install.</summary>
    public int UpstreamHttpPort { get; } = DefaultUpstreamHttpPort;

    /// <summary>Compose service name of the litd container, which doubles as its DNS name.</summary>
    public string RpcHost { get; }

    /// <summary>litd's <c>--httpslisten</c> port. litd serves gRPC there behind its self-signed cert.</summary>
    public int RpcPort { get; }

    /// <summary>
    /// litd's <c>--network</c>, which names the sub-directory holding the macaroon and log file.
    /// Only a hint: <see cref="LitdPaths"/> falls back to whatever network directory actually exists,
    /// because BTCPay and litd do not always spell a network the same way (testnet4, for instance).
    /// </summary>
    public string Network { get; }

    /// <summary>
    /// Basename of the custom fragment the operator is asked to add. The <c>.custom</c> suffix is
    /// what makes btcpayserver-docker keep the file across updates (it is gitignored there), and
    /// <c>btcpay-fragments</c> accepts it as an ordinary fragment name.
    /// </summary>
    public const string FragmentName = "opt-add-lightning-terminal-headless.custom";

    /// <summary>btcpayserver-docker's own Lightning Terminal fragment, which runs litd with its web UI.</summary>
    public const string UpstreamFragmentName = "opt-add-lightning-terminal";

    /// <summary>
    /// What <c>docker ps</c> calls the litd container, following btcpayserver-docker's convention of
    /// <c>btcpayserver_</c> plus the daemon's own name (btcpayserver_bitcoind, btcpayserver_lnd_bitcoin).
    /// </summary>
    /// <remarks>
    /// Only the container name. The Compose <em>service</em> stays <see cref="DefaultRpcHost"/>, which is
    /// what the service resolves to on the Docker network and what upstream's fragment also calls it -
    /// renaming that would move litd's DNS name and split this fragment from upstream's for anyone
    /// switching between the two.
    /// </remarks>
    public const string ContainerName = "btcpayserver_litd";

    /// <summary>Compose volume holding litd's data. Deliberately the same name upstream uses.</summary>
    public const string DataVolumeName = "lnd_lit_datadir";
}
