using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Configuration;
using BTCPayServer.Plugins.LightningTerminal.Services;
using BTCPayServer.Plugins.LightningTerminal.ViewModels;
using Grpc.Core;
using Litrpc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.LightningTerminal.Controllers;

/// <summary>
/// Server-administrator UI for Lightning Terminal: whether litd is installed and healthy, how to
/// install or remove it, how to pair Terminal on the web with it, and how to read its logs.
/// </summary>
/// <remarks>
/// Every action is gated on <see cref="Policies.CanModifyServerSettings"/>. litd holds LND's admin
/// macaroon, and a pairing phrase minted here is an admin credential for the node, so this is
/// server-owner territory - there is no read-only tier that would be safe to expose more widely.
/// </remarks>
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyServerSettings)]
[AutoValidateAntiforgeryToken]
[Route("server/plugins/lightning-terminal")]
public class UILightningTerminalController(
    LitdStatusService statusService,
    LitdClient client,
    LitdPaths paths,
    LitdFragment fragment,
    BTCPayServerOptions serverOptions) : Controller
{
    /// <summary>
    /// How long a pairing session stays valid. Matches litcli's own default, so a session minted here
    /// behaves the same as one minted with `litcli sessions add`.
    /// </summary>
    private static readonly TimeSpan SessionExpiry = TimeSpan.FromDays(90);

    /// <summary>
    /// The result view NewSession renders, which is not named after an action - so it is resolved by
    /// this name at render time, and a typo would surface only on form submit.
    /// </summary>
    public const string SessionCreatedView = "SessionCreated";

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var status = await statusService.GetStatusAsync(cancellationToken);

        var sessions = Array.Empty<TerminalSessionViewModel>();
        string? sessionsError = null;
        if (status.Running)
        {
            try
            {
                sessions = (await client.ListSessionsAsync(cancellationToken))
                    .OrderByDescending(session => session.ExpiryTimestampSeconds)
                    .Select(ToViewModel)
                    .ToArray();
            }
            catch (Exception ex) when (ex is RpcException or LitdNotReadyException)
            {
                sessionsError = ex is RpcException rpc ? LitdClient.Explain(rpc) : ex.Message;
            }
        }

        return View(new TerminalIndexViewModel
        {
            Status = status,
            Sessions = sessions,
            SessionsError = sessionsError,
            DockerDeployment = serverOptions.DockerDeployment,
            InstallCommand = fragment.InstallCommand
        });
    }

    /// <summary>
    /// The host commands for a given operation. Deliberately a separate page rather than a modal on
    /// the dashboard: installing and removing litd restarts the whole deployment, so an operator
    /// should read what they are about to run on its own screen.
    /// </summary>
    [HttpGet("instructions/{operation}")]
    public async Task<IActionResult> Instructions(TerminalOperation operation, CancellationToken cancellationToken)
    {
        // An unparseable route value binds to default(TerminalOperation) rather than failing, which
        // would quietly serve the install page for /instructions/nonsense.
        if (!Enum.IsDefined(operation) || !ModelState.IsValid)
            return NotFound();

        var status = await statusService.GetStatusAsync(cancellationToken);

        var blocker = operation switch
        {
            TerminalOperation.Switch when !status.UpstreamOnly => new InstallBlocker(
                "This deployment does not look like it runs BTCPay's own Lightning Terminal fragment, so " +
                "there is nothing to switch from."),
            TerminalOperation.Uninstall when !status.Installed => new InstallBlocker(
                "Lightning Terminal is not installed on this deployment, so there is nothing to remove."),
            _ => null
        };

        return View(new TerminalInstructionsViewModel
        {
            Operation = operation,
            Command = operation switch
            {
                TerminalOperation.Switch => fragment.SwitchCommand,
                TerminalOperation.Uninstall => fragment.UninstallCommand,
                TerminalOperation.Wipe => fragment.WipeCommand,
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            },
            DockerDeployment = serverOptions.DockerDeployment,
            Blocker = blocker
        });
    }

    /// <summary>
    /// Mints a fresh admin LNC session and sends the browser straight to Terminal on the web, paired.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A POST, and always a new session: a pairing phrase is single-use, so a GET that minted one would
    /// burn a credential on every refresh, prefetch or link preview.
    /// </para>
    /// <para>
    /// Redirecting rather than rendering the phrase keeps it out of a page the operator has to act on,
    /// and it stays out of Terminal's server logs either way - it rides in the URL fragment, which the
    /// browser never puts on the wire. The redirect host is always TerminalConnect.BaseUrl; nothing
    /// user-supplied reaches it, so this is not an open redirect.
    /// </para>
    /// </remarks>
    [HttpPost("connect")]
    public async Task<IActionResult> Connect(CancellationToken cancellationToken)
    {
        var label = $"BTCPay Server {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC";
        try
        {
            // The one-click path takes every default: admin, litcli's 90 days, the standard mailbox.
            var session = await client.AddSessionAsync(
                label, SessionType.TypeMacaroonAdmin, SessionExpiry,
                LitdClient.DefaultMailboxServer, accountId: null, cancellationToken);

            // Terminal has no business knowing which BTCPay instance sent the operator over.
            Response.Headers["Referrer-Policy"] = "no-referrer";
            return Redirect(TerminalConnect.PairingUrl(session));
        }
        catch (Exception ex) when (ex is RpcException or LitdNotReadyException)
        {
            TempData[WellKnownTempData.ErrorMessage] =
                $"Could not create a pairing session: {(ex is RpcException rpc ? LitdClient.Explain(rpc) : ex.Message)}";
            return RedirectToAction(nameof(Index));
        }
    }

    /// <summary>
    /// The manual counterpart to Connect: choose the session's label, lifetime, type and mailbox rather
    /// than taking the one-click defaults.
    /// </summary>
    [HttpGet("sessions/new")]
    public async Task<IActionResult> NewSession(CancellationToken cancellationToken)
    {
        var model = new NewSessionViewModel();
        await LoadAccountsAsync(model, cancellationToken);
        return View(model);
    }

    [HttpPost("sessions/new")]
    public async Task<IActionResult> NewSession(NewSessionViewModel model, CancellationToken cancellationToken)
    {
        if (!TerminalConnect.CreatableTypes.Contains(model.Type))
            ModelState.AddModelError(nameof(model.Type), "That session type cannot be created here.");

        // litd would reject this itself, but its error arrives after a round trip and reads like an
        // internal one; catching it here keeps the operator on the form with their other input intact.
        if (model.Type is SessionType.TypeMacaroonAccount && string.IsNullOrWhiteSpace(model.AccountId))
            ModelState.AddModelError(nameof(model.AccountId), "An account session has to name an account.");

        if (!ModelState.IsValid)
        {
            await LoadAccountsAsync(model, cancellationToken);
            return View(model);
        }

        try
        {
            var session = await client.AddSessionAsync(
                model.Label.Trim(),
                model.Type,
                TimeSpan.FromDays(model.ExpiryDays),
                model.MailboxServer.Trim(),
                model.AccountId,
                cancellationToken);

            return View(SessionCreatedView, new SessionCreatedViewModel
            {
                Label = session.Label,
                Type = TerminalConnect.TypeLabel(session.SessionType),
                Expiry = DateTimeOffset.FromUnixTimeSeconds((long)session.ExpiryTimestampSeconds),
                MailboxServer = session.MailboxServerAddr,
                PairingPhrase = session.PairingSecretMnemonic,
                PairingUrl = TerminalConnect.PairingUrl(session)
            });
        }
        catch (Exception ex) when (ex is RpcException or LitdNotReadyException)
        {
            ModelState.AddModelError(string.Empty,
                ex is RpcException rpc ? LitdClient.Explain(rpc) : ex.Message);
            await LoadAccountsAsync(model, cancellationToken);
            return View(model);
        }
    }

    /// <summary>
    /// Fills the account dropdown. Deliberately forgiving: a node that has never used accounts has none,
    /// and litd refusing to list them is no reason to block an admin or read-only session.
    /// </summary>
    private async Task LoadAccountsAsync(NewSessionViewModel model, CancellationToken cancellationToken)
    {
        try
        {
            model.Accounts = (await client.ListAccountsAsync(cancellationToken))
                .Select(account => new AccountOption(
                    account.Id,
                    string.IsNullOrWhiteSpace(account.Label) ? account.Id : $"{account.Label} ({account.Id})"))
                .OrderBy(account => account.Description, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is RpcException or LitdNotReadyException)
        {
            model.Accounts = [];
        }
    }

    [HttpPost("sessions/revoke")]
    public async Task<IActionResult> RevokeSession(string localPublicKey, CancellationToken cancellationToken)
    {
        if (!TryParseHex(localPublicKey, out var parsed))
        {
            TempData[WellKnownTempData.ErrorMessage] = "That is not a valid session key.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            await client.RevokeSessionAsync(parsed, cancellationToken);
            TempData[WellKnownTempData.SuccessMessage] = "Session revoked.";
        }
        catch (Exception ex) when (ex is RpcException or LitdNotReadyException)
        {
            TempData[WellKnownTempData.ErrorMessage] =
                $"Could not revoke the session: {(ex is RpcException rpc ? LitdClient.Explain(rpc) : ex.Message)}";
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("logs")]
    public IActionResult Logs()
    {
        var logFile = paths.LogFile;
        if (logFile is null)
        {
            TempData[WellKnownTempData.ErrorMessage] = "litd has not written a log file yet.";
            return RedirectToAction(nameof(Index));
        }

        // FileShare.ReadWrite because litd holds the file open and keeps appending to it; without
        // this the download fails for the entire life of the container.
        var stream = new FileStream(
            logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
        return File(stream, "text/plain", $"litd-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.log");
    }

    private static TerminalSessionViewModel ToViewModel(Litrpc.Session session)
    {
        var paired = !session.RemotePublicKey.IsEmpty;
        var active = session.SessionState is Litrpc.SessionState.StateCreated or Litrpc.SessionState.StateInUse;
        return new TerminalSessionViewModel
        {
            Label = session.Label,
            State = TerminalConnect.StateLabel(session.SessionState),
            Type = TerminalConnect.TypeLabel(session.SessionType),
            Expiry = DateTimeOffset.FromUnixTimeSeconds((long)session.ExpiryTimestampSeconds),
            LocalPublicKey = Convert.ToHexString(session.LocalPublicKey.ToByteArray()).ToLowerInvariant(),
            Paired = paired,
            Active = active,
            PairingUrl = active && !paired ? TerminalConnect.PairingUrl(session) : null
        };
    }

    private static bool TryParseHex(string? value, out byte[] parsed)
    {
        parsed = [];
        if (string.IsNullOrWhiteSpace(value))
            return false;
        try
        {
            parsed = Convert.FromHexString(value.Trim());
            return parsed.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
