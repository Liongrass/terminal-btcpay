using System.Buffers.Binary;

namespace BTCPayServer.Plugins.LightningTerminal.Services;

/// <summary>
/// Reproduces the macaroon litd hands out when an account is created, so one can be shown again later
/// without ever being stored.
/// </summary>
/// <remarks>
/// <para>
/// litd returns an account's macaroon exactly once, in <c>CreateAccountResponse</c>, and has no RPC to
/// fetch it again. Re-deriving it means following the same recipe
/// (<c>accounts/rpcserver.go</c>, <c>CreateAccount</c>): bake against a root key derived from the
/// account's own ID with the account permission set, then narrow the result with the account caveat.
/// Everything is deterministic, so the same account always yields the same macaroon.
/// </para>
/// <para>
/// <strong>litd's own <c>BakeSuperMacaroon</c> RPC is not a shortcut for this.</strong> It takes the
/// same 4-byte root key suffix, which makes it look like the right call, but it bakes with
/// <c>permsMgr.ActivePermissions</c> and <c>caveats = nil</c>. An account is scoped by its caveat and
/// nothing else, so that RPC returns a full-node super macaroon - valid, accepted by litd, and
/// carrying vastly more authority than the account it would be labelled with.
/// </para>
/// </remarks>
public class AccountMacaroon(LndClient lnd)
{
    /// <summary>
    /// Marks a root key ID as belonging to a super macaroon - litd's <c>SuperMacaroonRootKeyPrefix</c>.
    /// </summary>
    private static readonly byte[] SuperMacaroonRootKeyPrefix = [0xFF, 0xEE, 0xDD, 0xCC];

    /// <summary>
    /// litd's <c>accounts.MacaroonPermissions</c>: what an account-scoped macaroon may do before the
    /// caveat narrows it to one account's balance.
    /// </summary>
    /// <remarks>
    /// Mirrored, not derived - litd does not expose this set over RPC. If litd ever changes it, a
    /// macaroon re-derived here would differ from one litd issued at creation, so it is worth
    /// re-checking against accounts/interface.go when bumping the pinned litd image.
    /// </remarks>
    public static readonly IReadOnlyList<(string Entity, string Action)> Permissions =
    [
        ("info", "read"),
        ("offchain", "read"),
        ("offchain", "write"),
        ("onchain", "read"),
        ("invoices", "read"),
        ("invoices", "write"),
        ("peers", "read")
    ];

    /// <summary>
    /// The root key ID litd bakes an account's macaroon under: the super-macaroon prefix followed by
    /// the first four bytes of the account's ID, read as a big-endian ulong.
    /// </summary>
    public static ulong RootKeyIdFor(string accountIdHex)
    {
        var accountId = ParseAccountId(accountIdHex);

        Span<byte> rootKey = stackalloc byte[8];
        SuperMacaroonRootKeyPrefix.CopyTo(rootKey);
        accountId.AsSpan(0, 4).CopyTo(rootKey[4..]);

        return BinaryPrimitives.ReadUInt64BigEndian(rootKey);
    }

    /// <summary>
    /// The caveat that scopes a macaroon to one account, in lnd's custom-caveat form:
    /// <c>lnd-custom account &lt;hex id&gt;</c>.
    /// </summary>
    public static string CaveatFor(string accountIdHex) =>
        $"lnd-custom account {Convert.ToHexString(ParseAccountId(accountIdHex)).ToLowerInvariant()}";

    /// <summary>Bakes and narrows, returning the hex macaroon for <paramref name="accountIdHex"/>.</summary>
    public async Task<string> BakeAsync(string accountIdHex, CancellationToken cancellationToken)
    {
        var baked = await lnd.BakeMacaroonAsync(
            RootKeyIdFor(accountIdHex), Permissions, cancellationToken);

        // lnd's BakeMacaroon RPC has no caveat field, so the narrowing happens here - the same split
        // lncli makes between `bakemacaroon` and `restrictmacaroon`.
        var macaroon = Macaroon.FromHex(baked);
        macaroon.AddFirstPartyCaveat(CaveatFor(accountIdHex));
        return macaroon.ToHex();
    }

    /// <summary>litd's account IDs are 8 bytes, hex encoded.</summary>
    private static byte[] ParseAccountId(string accountIdHex)
    {
        if (accountIdHex.Length != AccountRules.AccountIdHexLength)
            throw new FormatException(
                $"An account ID is {AccountRules.AccountIdHexLength} hex characters, got {accountIdHex.Length}.");

        return Convert.FromHexString(accountIdHex);
    }
}
