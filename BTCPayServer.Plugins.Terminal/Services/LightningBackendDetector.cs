using BTCPayServer.Configuration;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.Terminal.Services;

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

/// <param name="Kind">What this plugin is willing to do with the backend it found.</param>
/// <param name="Type">The <c>type=</c> of the internal connection string, e.g. <c>lnd-rest</c> or <c>clightning</c>.</param>
/// <param name="Server">The <c>server=</c> of the internal connection string, if it had one.</param>
public record LightningBackend(LightningBackendKind Kind, string? Type, string? Server)
{
    public bool CanRunLitd => Kind is LightningBackendKind.BundledLnd;

    /// <summary>Why litd cannot be installed, phrased for an operator. Null when it can.</summary>
    public string? Blocker => Kind switch
    {
        LightningBackendKind.BundledLnd => null,
        LightningBackendKind.None =>
            "This deployment has no internal Lightning node. Lightning Terminal drives LND, so set " +
            "BTCPAYGEN_LIGHTNING=lnd and re-run btcpay-setup.sh before installing it.",
        LightningBackendKind.ExternalLnd =>
            $"This deployment's Lightning node is an LND that lives outside the deployment ({Server}). " +
            "Lightning Terminal is installed as a container alongside BTCPay's own LND and connects to it " +
            "over the internal Docker network, so it cannot be pointed at an external node from here.",
        _ =>
            $"This deployment's Lightning node is {Describe(Type)}, not LND. Lightning Terminal only supports LND."
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
