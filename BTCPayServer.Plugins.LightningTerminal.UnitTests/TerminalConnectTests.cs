using System.Text;
using BTCPayServer.Plugins.LightningTerminal.Services;
using Litrpc;
using Xunit;

namespace BTCPayServer.Plugins.LightningTerminal.UnitTests;

/// <summary>
/// Pins the pairing-link encoding to litd's own. Terminal parses this strictly, and it is produced in
/// a different language here than in litd's UI, so there is nothing else keeping the two in step.
/// </summary>
public class TerminalConnectTests
{
    private static Session SessionWith(SessionType type) => new()
    {
        PairingSecretMnemonic = "absorb sample tape gospel stereo",
        MailboxServerAddr = "mailbox.terminal.lightning.today:443",
        SessionType = type
    };

    [Fact]
    public void PairingUrlMatchesLitdsOwnEncoding()
    {
        var url = TerminalConnect.PairingUrl(SessionWith(SessionType.TypeMacaroonAdmin));

        // litd's app/src/store/models/session.ts: base64(ascii("phrase||mailbox||typeLabel")) on the
        // #/connect/pair/ hash route.
        var expected = Convert.ToBase64String(Encoding.ASCII.GetBytes(
            "absorb sample tape gospel stereo||mailbox.terminal.lightning.today:443||Admin"));
        Assert.Equal($"https://terminal.lightning.engineering#/connect/pair/{expected}", url);
    }

    [Fact]
    public void PairingPhraseStaysInTheUrlFragment()
    {
        var url = TerminalConnect.PairingUrl(SessionWith(SessionType.TypeMacaroonAdmin));

        // Everything after '#' stays in the browser, so the phrase is never sent to Terminal's server.
        // A path or query would put an admin credential in someone's web server logs.
        var hash = url.IndexOf('#');
        Assert.InRange(hash, 0, url.Length - 1);
        Assert.DoesNotContain("?", url[..hash]);
        Assert.EndsWith("terminal.lightning.engineering", url[..hash]);
    }

    [Theory]
    [InlineData(SessionType.TypeMacaroonAdmin, "Admin")]
    [InlineData(SessionType.TypeMacaroonReadonly, "Read-Only")]
    [InlineData(SessionType.TypeMacaroonCustom, "Custom")]
    [InlineData(SessionType.TypeMacaroonAccount, "Custodial")]
    [InlineData(SessionType.TypeUiPassword, "LiT UI Password")]
    public void TypeLabelsMatchTerminalsVocabulary(SessionType type, string expected) =>
        Assert.Equal(expected, TerminalConnect.TypeLabel(type));
}

/// <summary>
/// Which session types the manual form is allowed to offer.
/// </summary>
public class CreatableSessionTypeTests
{
    [Fact]
    public void CustomIsNotOffered()
    {
        // litd's AddSession returns "custom macaroon permissions must be specified for the custom
        // macaroon session type" unless the request carries explicit permissions. With no permissions
        // editor here, offering it would only ever produce that error.
        Assert.DoesNotContain(SessionType.TypeMacaroonCustom, TerminalConnect.CreatableTypes);
    }

    [Fact]
    public void LitdsOwnSessionTypesAreNotOffered()
    {
        // litd mints these itself - a UI-password session for its web UI, which this plugin disables,
        // and autopilot sessions for the Autopilot server.
        Assert.DoesNotContain(SessionType.TypeUiPassword, TerminalConnect.CreatableTypes);
        Assert.DoesNotContain(SessionType.TypeAutopilot, TerminalConnect.CreatableTypes);
    }

    [Fact]
    public void TheTypesThatNeedNoExtraInputAreOffered()
    {
        Assert.Contains(SessionType.TypeMacaroonAdmin, TerminalConnect.CreatableTypes);
        Assert.Contains(SessionType.TypeMacaroonReadonly, TerminalConnect.CreatableTypes);
        // Account needs an id, which the form collects and validates before litd sees it.
        Assert.Contains(SessionType.TypeMacaroonAccount, TerminalConnect.CreatableTypes);
    }

    [Fact]
    public void EveryOfferedTypeHasALabelTerminalUnderstands()
    {
        foreach (var type in TerminalConnect.CreatableTypes)
            Assert.NotEqual("Unknown", TerminalConnect.TypeLabel(type));
    }

    [Fact]
    public void TheFormStartsOnTheSameDefaultsAsOneClickConnect()
    {
        var model = new ViewModels.NewSessionViewModel();

        Assert.Equal(SessionType.TypeMacaroonAdmin, model.Type);
        Assert.Equal(LitdClient.DefaultMailboxServer, model.MailboxServer);
        // litcli's own default expiry, so a phrase minted here behaves like one minted with litcli.
        Assert.Equal(90, model.ExpiryDays);
    }
}
