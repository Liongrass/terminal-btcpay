using BTCPayServer.Configuration;
using Microsoft.Extensions.Configuration;

namespace BTCPayServer.Plugins.Terminal.Services;

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
            configuration["terminallitrpchost"] ?? "lnd_lit",
            int.TryParse(configuration["terminallitrpcport"], out var port) ? port : 8443,
            configuration["terminallitnetwork"] ?? serverOptions.NetworkType.ToString())
    {
    }

    internal TerminalOptions(string litDataDirectory, string rpcHost, int rpcPort, string network)
    {
        LitDataDirectory = litDataDirectory;
        RpcHost = rpcHost;
        RpcPort = rpcPort;
        Network = network.ToLowerInvariant();
    }

    /// <summary>litd's <c>--lit-dir</c> (<c>lnd_lit_datadir</c>) as mounted into this container.</summary>
    public string LitDataDirectory { get; }

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

    /// <summary>Compose volume holding litd's data. Deliberately the same name upstream uses.</summary>
    public const string DataVolumeName = "lnd_lit_datadir";
}
