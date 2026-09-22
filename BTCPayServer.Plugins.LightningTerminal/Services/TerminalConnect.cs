using System.Text;
using Litrpc;

namespace BTCPayServer.Plugins.LightningTerminal.Services;

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

    /// <summary>
    /// The label one-click Connect gives a session, since it asks for none.
    /// </summary>
    /// <remarks>
    /// Date only, no clock time: litd puts no unique constraint on session labels - the sessions table
    /// uniques alias and local_public_key, and it is the accounts table that uniques label - so two
    /// sessions minted on the same day may share one, and the list tells them apart by state and expiry.
    /// </remarks>
    public static string DefaultSessionLabel(DateTimeOffset now) => $"Lightning Terminal {now:yyyy-MM-dd}";

    public static string PairingUrl(Session session)
    {
        var payload = $"{session.PairingSecretMnemonic}||{session.MailboxServerAddr}||{TypeLabel(session.SessionType)}";
        return $"{BaseUrl}#/connect/pair/{Convert.ToBase64String(Encoding.ASCII.GetBytes(payload))}";
    }

    /// <summary>
    /// The session types this plugin offers, with the labels Terminal uses for them.
    /// </summary>
    /// <remarks>
    /// Custom is missing on purpose: litd's AddSession rejects it outright unless the request carries
    /// explicit macaroon permissions ("custom macaroon permissions must be specified"), so offering it
    /// without a permissions editor would only ever produce an error. UI-password and autopilot
    /// sessions are litd's own to create, not an operator's.
    /// </remarks>
    public static IReadOnlyList<SessionType> CreatableTypes { get; } =
    [
        SessionType.TypeMacaroonAdmin,
        SessionType.TypeMacaroonReadonly,
        SessionType.TypeMacaroonAccount
    ];

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
