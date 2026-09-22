using BTCPayServer.Configuration;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.LightningTerminal.Services;

public enum LightningBackendKind
{
    /// <summary>The deployment has no internal Lightning node at all.</summary>
    None,

    /// <summary>btcpayserver-docker's own LND (the <c>bitcoin-lnd</c> fragment). The only one litd can be attached to.</summary>
    BundledLnd,

    /// <summary>An LND that is not part of this deployment, so not reachable at <c>lnd_bitcoin</c> from a sibling container.</summary>
    ExternalLnd,

    /// <summary>Core Lightning, Eclair, phoenixd, Lightning Charge - anything litd cannot drive.</summary>
    Other
}

/// <summary>Why something cannot be installed, and - when there is one - what to run to fix it.</summary>
/// <param name="Message">Plain prose. Rendered as text, so it carries no markup of its own.</param>
/// <param name="Command">
/// Shell to paste into a root shell on the host, or null when the blocker is not something a command
/// resolves. Rendered as a copyable block rather than inline, the same way the install instructions are.
/// </param>
public record InstallBlocker(string Message, string? Command = null);

/// <param name="Kind">What this plugin is willing to do with the backend it found.</param>
/// <param name="Type">The <c>type=</c> of the internal connection string, e.g. <c>lnd-rest</c> or <c>clightning</c>.</param>
/// <param name="Server">The <c>server=</c> of the internal connection string, if it had one.</param>
public record LightningBackend(LightningBackendKind Kind, string? Type, string? Server)
{
    public bool CanRunLitd => Kind is LightningBackendKind.BundledLnd;

    /// <summary>Why litd cannot be installed, phrased for an operator. Null when it can.</summary>
    public InstallBlocker? Blocker => Kind switch
    {
        LightningBackendKind.BundledLnd => null,
        LightningBackendKind.None => new InstallBlocker(
            "This server has no internal Lightning node. Lightning Terminal requires LND to be already " +
            "running. To install LND on your BTCPay Server, follow these steps as root in your " +
            "btcpayserver-docker directory:",
            // The one blocker an operator can act on immediately, so it ships the commands. The cd is
            // absolute rather than relative: the prose says which directory to be in, but a block that
            // only works from the right working directory is a block that silently runs in the wrong one.
            """
            export BTCPAYGEN_LIGHTNING=lnd
            cd "$BTCPAY_BASE_DIRECTORY/btcpayserver-docker"
            . ./btcpay-setup.sh -i
            """),
        LightningBackendKind.ExternalLnd => new InstallBlocker(
            $"This deployment's Lightning node is an LND that lives outside the deployment ({Server}). " +
            "Lightning Terminal is installed as a container alongside BTCPay's own LND and connects to it " +
            "over the internal Docker network, so it cannot be pointed at an external node from here."),
        _ => new InstallBlocker(
            $"This deployment's Lightning node is {Describe(Type)}, not LND. Lightning Terminal only supports LND.")
    };

    private static string Describe(string? type) => type switch
    {
        null => "of an unrecognised type",
        "clightning" => "Core Lightning",
        "eclair" => "Eclair",
        "phoenixd" => "phoenixd",
        "charge" => "Lightning Charge",
        _ => $"'{type}'"
    };
}

/// <summary>
/// Works out which Lightning implementation this deployment runs, from BTCPay's own internal node
/// configuration rather than by probing the Docker network.
/// </summary>
/// <remarks>
/// <c>BTCPAY_BTCLIGHTNING</c> is set by whichever Lightning fragment is selected
/// (<c>bitcoin-lnd</c> writes an <c>lnd-rest</c> connection string pointing at <c>lnd_bitcoin</c>,
/// <c>bitcoin-clightning</c> writes a <c>clightning</c> one, and so on), so reading it back tells us
/// exactly what the deployment generator decided - no host access and no DNS guessing needed.
/// </remarks>
public class LightningBackendDetector(IOptions<LightningNetworkOptions> lightningOptions)
{
    /// <summary>Compose service name of btcpayserver-docker's bundled LND, and so its DNS name.</summary>
    public const string BundledLndHost = "lnd_bitcoin";

    public LightningBackend Detect()
    {
        if (!lightningOptions.Value.InternalLightningByCryptoCode.TryGetValue("BTC", out var client))
            return new LightningBackend(LightningBackendKind.None, null, null);

        // ILightningClient.ToString() round-trips to the connection string it was built from - this
        // is how core itself reads the internal node back (see LightningListener.GetInternalNode).
        return Classify(client.ToString() ?? string.Empty);
    }

    internal static LightningBackend Classify(string connectionString)
    {
        var parts = ParseConnectionString(connectionString);
        parts.TryGetValue("type", out var type);
        parts.TryGetValue("server", out var server);

        if (type is null || !type.StartsWith("lnd-", StringComparison.OrdinalIgnoreCase))
            return new LightningBackend(LightningBackendKind.Other, type, server);

        return new LightningBackend(
            IsBundledLnd(server) ? LightningBackendKind.BundledLnd : LightningBackendKind.ExternalLnd, type, server);
    }

    private static bool IsBundledLnd(string? server) =>
        Uri.TryCreate(server, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Host, BundledLndHost, StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string> ParseConnectionString(string connectionString)
    {
        var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = segment.IndexOf('=');
            if (separator <= 0)
                continue;
            parts[segment[..separator].Trim()] = segment[(separator + 1)..].Trim();
        }
        return parts;
    }
}
