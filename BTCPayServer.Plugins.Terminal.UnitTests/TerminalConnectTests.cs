using System.Text;
using BTCPayServer.Plugins.Terminal.Services;
using Litrpc;
using Xunit;

namespace BTCPayServer.Plugins.Terminal.UnitTests;

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
