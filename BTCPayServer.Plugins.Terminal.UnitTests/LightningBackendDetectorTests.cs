using BTCPayServer.Plugins.Terminal.Services;
using Xunit;

namespace BTCPayServer.Plugins.Terminal.UnitTests;

/// <summary>
/// The install gate. litd is only ever attached to btcpayserver-docker's own LND, so anything else
/// has to be refused before an operator pastes a fragment that cannot work.
/// </summary>
public class LightningBackendDetectorTests
{
    /// <summary>Exactly what btcpayserver-docker's bitcoin-lnd.yml writes into BTCPAY_BTCLIGHTNING.</summary>
    private const string BundledLnd =
        "type=lnd-rest;server=http://lnd_bitcoin:8080/;macaroonfilepath=/etc/lnd_bitcoin/admin.macaroon;allowinsecure=true";

    [Fact]
    public void AcceptsTheBundledLnd()
    {
        var backend = LightningBackendDetector.Classify(BundledLnd);

        Assert.Equal(LightningBackendKind.BundledLnd, backend.Kind);
        Assert.True(backend.CanRunLitd);
        Assert.Null(backend.Blocker);
    }

    [Fact]
    public void AcceptsTheBundledLndOverGrpcToo()
    {
        var backend = LightningBackendDetector.Classify(
            "type=lnd-grpc;server=https://lnd_bitcoin:10009/;macaroonfilepath=/etc/lnd_bitcoin/admin.macaroon");

        Assert.Equal(LightningBackendKind.BundledLnd, backend.Kind);
    }

    [Fact]
    public void RefusesAnLndOutsideTheDeployment()
    {
        // litd is wired to lnd_bitcoin:10009 over the internal Docker network, so an LND anywhere else
        // is unreachable no matter what BTCPay itself can talk to.
        var backend = LightningBackendDetector.Classify(
            "type=lnd-rest;server=https://lnd.example.com:8080/;macaroon=0201036c6e64");

        Assert.Equal(LightningBackendKind.ExternalLnd, backend.Kind);
        Assert.False(backend.CanRunLitd);
        Assert.Contains("lnd.example.com", backend.Blocker);
    }

    [Theory]
    [InlineData("type=clightning;server=unix://etc/clightning_bitcoin/lightning-rpc", "Core Lightning")]
    [InlineData("type=eclair;server=http://eclair:4570;password=hunter2", "Eclair")]
    [InlineData("type=phoenixd;server=http://phoenixd:9740/;password=hunter2", "phoenixd")]
    [InlineData("type=charge;server=https://charge.example.com;api-token=hunter2", "Lightning Charge")]
    public void RefusesEveryOtherImplementationByName(string connectionString, string expectedName)
    {
        var backend = LightningBackendDetector.Classify(connectionString);

        Assert.Equal(LightningBackendKind.Other, backend.Kind);
        Assert.False(backend.CanRunLitd);
        Assert.Contains(expectedName, backend.Blocker);
    }

    [Fact]
    public void RefusesWhenThereIsNoConnectionStringAtAll()
    {
        var backend = LightningBackendDetector.Classify(string.Empty);

        Assert.Equal(LightningBackendKind.Other, backend.Kind);
        Assert.False(backend.CanRunLitd);
        Assert.NotNull(backend.Blocker);
    }

    [Fact]
    public void RefusesAnLndWithNoServerToCheck()
    {
        // No server means nothing says it is the bundled one, and guessing yes would hand the operator
        // a fragment that silently points at the wrong node.
        var backend = LightningBackendDetector.Classify("type=lnd-rest;macaroonfilepath=/etc/lnd/admin.macaroon");

        Assert.Equal(LightningBackendKind.ExternalLnd, backend.Kind);
        Assert.False(backend.CanRunLitd);
    }

    [Fact]
    public void NoInternalNodeIsItsOwnRefusal()
    {
        // Distinct from "wrong implementation": the fix is to deploy LND, not to swap one out.
        var backend = new LightningBackend(LightningBackendKind.None, null, null);

        Assert.False(backend.CanRunLitd);
        Assert.Contains("BTCPAYGEN_LIGHTNING=lnd", backend.Blocker);
    }
}
