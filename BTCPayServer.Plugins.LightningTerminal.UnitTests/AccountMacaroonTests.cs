using BTCPayServer.Plugins.LightningTerminal.Services;
using Xunit;

namespace BTCPayServer.Plugins.LightningTerminal.UnitTests;

/// <summary>
/// The recipe that reproduces litd's account macaroon. Every value here is checked against litd's own
/// Go source, because a macaroon that is wrong in the right shape is still a working credential.
/// </summary>
public class AccountMacaroonTests
{
    [Theory]
    // Computed with litd's NewSuperMacaroonRootKeyID over the account ID's first four bytes.
    [InlineData("0011223344556677", 18441921392372949555UL)]
    [InlineData("ffffffffffffffff", 18441921396666793983UL)]
    [InlineData("0000000000000000", 18441921392371826688UL)]
    [InlineData("a4b2c3d4e5f60718", 18441921395135005652UL)]
    public void TheRootKeyMatchesLitdsDerivation(string accountId, ulong expected) =>
        Assert.Equal(expected, AccountMacaroon.RootKeyIdFor(accountId));

    [Fact]
    public void OnlyTheFirstFourBytesOfTheAccountIdReachTheRootKey()
    {
        // litd copies account.ID[0:4] into the suffix, so two accounts sharing a prefix share a root
        // key. That is litd's design - the caveat, not the root key, is what separates them.
        Assert.Equal(
            AccountMacaroon.RootKeyIdFor("aabbccdd00000000"),
            AccountMacaroon.RootKeyIdFor("aabbccddffffffff"));
    }

    [Fact]
    public void TheCaveatIsLndsCustomAccountCondition()
    {
        // checkers.Condition("lnd-custom", "account <hex>") joins with a single space, and litd
        // formats the ID with %x - lower case, no separators.
        Assert.Equal("lnd-custom account 0011223344556677", AccountMacaroon.CaveatFor("0011223344556677"));
        Assert.Equal("lnd-custom account aabbccddeeff0011", AccountMacaroon.CaveatFor("AABBCCDDEEFF0011"));
    }

    [Theory]
    [InlineData("00112233445566")]      // 7 bytes
    [InlineData("001122334455667788")]  // 9 bytes
    [InlineData("")]
    [InlineData("not hex at all!!")]
    public void AMalformedAccountIdIsRejected(string accountId)
    {
        // Silently truncating or padding would bake a macaroon for a different account than the one
        // the operator clicked on.
        Assert.ThrowsAny<FormatException>(() => AccountMacaroon.RootKeyIdFor(accountId));
    }

    [Fact]
    public void ThePermissionSetMirrorsLitdsAccountPermissions()
    {
        // litd's accounts.MacaroonPermissions, in order. Not derivable over RPC, so it is mirrored -
        // if litd changes it, a macaroon baked here stops matching the one litd issued at creation.
        Assert.Equal(
            [("info", "read"), ("offchain", "read"), ("offchain", "write"), ("onchain", "read"),
             ("invoices", "read"), ("invoices", "write"), ("peers", "read")],
            AccountMacaroon.Permissions);
    }

    [Fact]
    public void ThePermissionSetGrantsNoMacaroonAuthority()
    {
        // An account macaroon that could bake further macaroons would let its holder mint themselves
        // an unrestricted one, and the balance cap would mean nothing.
        Assert.DoesNotContain(AccountMacaroon.Permissions, permission => permission.Entity == "macaroon");
    }
}
