using System.Text;
using Litrpc;

namespace BTCPayServer.Plugins.Terminal.Services;

/// <summary>
/// Builds the Terminal-on-the-web link that pairs a browser with this node over Lightning Node
/// Connect, given a session minted by litd.
/// </summary>
/// <remarks>
/// The encoding is litd's own: its web UI base64s <c>phrase||mailbox||typeLabel</c> and hangs it off
/// the <c>#/connect/pair/</c> hash route (see <c>app/src/store/models/session.ts</c> upstream). It is
/// reproduced here rather than approximated, because Terminal parses it strictly - and it is a hash
/// route, so the pairing phrase stays in the browser and is never sent to the Terminal web server.
/// </remarks>
public static class TerminalConnect
{
    public const string BaseUrl = "https://terminal.lightning.engineering";

    public static string PairingUrl(Session session)
    {
        var payload = $"{session.PairingSecretMnemonic}||{session.MailboxServerAddr}||{TypeLabel(session.SessionType)}";
        return $"{BaseUrl}#/connect/pair/{Convert.ToBase64String(Encoding.ASCII.GetBytes(payload))}";
    }

    /// <summary>The human-readable session type Terminal expects in the third field of the payload.</summary>
    public static string TypeLabel(SessionType type) => type switch
    {
        SessionType.TypeMacaroonReadonly => "Read-Only",
        SessionType.TypeMacaroonAdmin => "Admin",
        SessionType.TypeMacaroonCustom => "Custom",
        SessionType.TypeMacaroonAccount => "Custodial",
        SessionType.TypeUiPassword => "LiT UI Password",
        _ => "Unknown"
    };

    public static string StateLabel(SessionState state) => state switch
    {
        SessionState.StateCreated => "Created",
        SessionState.StateInUse => "In Use",
        SessionState.StateRevoked => "Revoked",
        SessionState.StateExpired => "Expired",
        SessionState.StateReserved => "Reserved",
        _ => "Unknown"
    };
}
