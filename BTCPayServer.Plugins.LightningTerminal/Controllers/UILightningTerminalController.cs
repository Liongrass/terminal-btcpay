using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Contracts;
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
    AccountMacaroon accountMacaroon,
    LitdConnection connection,
    ISettingsRepository settingsRepository,
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

        var accounts = Array.Empty<AccountViewModel>();
        string? accountsError = null;
        if (status.Running)
        {
            try
            {
                accounts = (await client.ListAccountsAsync(cancellationToken))
                    .Select(ToViewModel)
                    .OrderBy(account => account.Display, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception ex) when (ex is RpcException or LitdNotReadyException)
            {
                accountsError = ex is RpcException rpc ? LitdClient.Explain(rpc) : ex.Message;
            }
        }

        return View(new TerminalIndexViewModel
        {
            Status = status,
            Sessions = sessions,
            SessionsError = sessionsError,
            Accounts = accounts,
            AccountsError = accountsError,
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
        var label = TerminalConnect.DefaultSessionLabel(DateTimeOffset.UtcNow);
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

    [HttpGet("accounts/new")]
    public IActionResult NewAccount() => View(new NewAccountViewModel());

    [HttpPost("accounts/new")]
    public async Task<IActionResult> NewAccount(NewAccountViewModel model, CancellationToken cancellationToken)
    {
        var label = model.Label?.Trim();

        if (AccountRules.LooksLikeAnAccountId(label))
            ModelState.AddModelError(nameof(model.Label),
                $"A label of {AccountRules.AccountIdHexLength} hex characters would be mistaken for an account ID.");

        if (!ModelState.IsValid)
            return View(model);

        try
        {
            var expiry = model.Expiry is { } date ? AccountRules.EndOfDayUtc(date) : (DateTimeOffset?)null;

            var account = await client.CreateAccountAsync(
                (ulong)model.BalanceSats, expiry, label, cancellationToken);

            TempData[WellKnownTempData.SuccessMessage] = $"Account {ToViewModel(account).Display} created.";
            return RedirectToAction(nameof(Account), new { id = account.Id });
        }
        catch (Exception ex) when (ex is RpcException or LitdNotReadyException)
        {
            ModelState.AddModelError(string.Empty,
                ex is RpcException rpc ? LitdClient.Explain(rpc) : ex.Message);
            return View(model);
        }
    }

    [HttpGet("accounts/{id}")]
    public async Task<IActionResult> Account(string id, CancellationToken cancellationToken)
    {
        try
        {
            return View(await DetailViewModelAsync(id, macaroon: null, cancellationToken));
        }
        catch (Exception ex) when (ex is RpcException or LitdNotReadyException)
        {
            TempData[WellKnownTempData.ErrorMessage] =
                ex is RpcException rpc ? LitdClient.Explain(rpc) : ex.Message;
            return RedirectToAction(nameof(Index));
        }
    }

    /// <summary>
    /// Re-derives this account's macaroon and shows it.
    /// </summary>
    /// <remarks>
    /// A POST, even though nothing is stored: it hands out a credential that can spend, and a GET
    /// would put that in browser history, prefetchers and link previews. Nothing is persisted - litd
    /// returns an account's macaroon only once, at creation, so this bakes it again from the account's
    /// own ID. See AccountMacaroon for why litd's BakeSuperMacaroon RPC is not the shortcut it looks.
    /// </remarks>
    [HttpPost("accounts/{id}/macaroon")]
    public async Task<IActionResult> RevealAccountMacaroon(string id, CancellationToken cancellationToken)
    {
        try
        {
            var macaroon = await accountMacaroon.BakeAsync(id, cancellationToken);
            return View(nameof(Account), await DetailViewModelAsync(id, macaroon, cancellationToken));
        }
        catch (Exception ex) when (ex is RpcException or LitdNotReadyException or FormatException)
        {
            TempData[WellKnownTempData.ErrorMessage] =
                $"Could not bake the macaroon: {(ex is RpcException rpc ? LitdClient.Explain(rpc) : ex.Message)}";
            return RedirectToAction(nameof(Account), new { id });
        }
    }

    private async Task<AccountDetailViewModel> DetailViewModelAsync(
        string id, string? macaroon, CancellationToken cancellationToken)
    {
        var account = await client.AccountInfoAsync(id, cancellationToken);
        return new AccountDetailViewModel
        {
            Account = ToViewModel(account),
            LastUpdate = DateTimeOffset.FromUnixTimeSeconds(account.LastUpdate),
            InvoiceHashes = account.Invoices.Select(invoice => Hex(invoice.Hash)).ToList(),
            Payments = account.Payments
                .Select(payment => new AccountPaymentViewModel(
                    Hex(payment.Hash), payment.State, payment.FullAmount))
                .ToList(),
            Macaroon = macaroon
        };
    }

    [HttpPost("accounts/{id}/remove")]
    public async Task<IActionResult> RemoveAccount(string id, CancellationToken cancellationToken)
    {
        try
        {
            await client.RemoveAccountAsync(id, cancellationToken);
            TempData[WellKnownTempData.SuccessMessage] = "Account removed.";
        }
        catch (Exception ex) when (ex is RpcException or LitdNotReadyException)
        {
            TempData[WellKnownTempData.ErrorMessage] =
                $"Could not remove the account: {(ex is RpcException rpc ? LitdClient.Explain(rpc) : ex.Message)}";
        }
        return RedirectToAction(nameof(Index));
    }

    private static AccountViewModel ToViewModel(Litrpc.Account account) => new(
        account.Id,
        account.Label,
        account.InitialBalance,
        account.CurrentBalance,
        // litd writes zero for an account that never expires.
        account.ExpirationDate > 0 ? DateTimeOffset.FromUnixTimeSeconds(account.ExpirationDate) : null);

    private static string Hex(Google.Protobuf.ByteString bytes) =>
        Convert.ToHexString(bytes.ToByteArray()).ToLowerInvariant();

    /// <summary>
    /// Collects litd's UI password, which an upstream install needs in place of a macaroon.
    /// </summary>
    [HttpGet("settings")]
    public IActionResult Settings()
    {
        var info = connection.Describe();
        return View(new TerminalSettingsViewModel
        {
            HasUiPassword = info.HasUiPassword,
            UpstreamUrl = info.UpstreamUrl
        });
    }

    [HttpPost("settings")]
    public async Task<IActionResult> Settings(TerminalSettingsViewModel model)
    {
        var settings = await settingsRepository.GetSettingAsync<LightningTerminalSettings>()
                       ?? new LightningTerminalSettings();

        // An empty box leaves the stored password alone rather than clearing it - the form never
        // echoes it back, so submitting the page for any other reason would otherwise wipe it.
        var submitted = model.UiPassword?.Trim();
        if (!string.IsNullOrEmpty(submitted))
        {
            settings.UiPassword = submitted;
            await settingsRepository.UpdateSetting(settings);
            TempData[WellKnownTempData.SuccessMessage] = "Password saved.";
        }
        else
        {
            TempData[WellKnownTempData.SuccessMessage] = "Nothing changed.";
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("settings/forget")]
    public async Task<IActionResult> ForgetUiPassword()
    {
        var settings = await settingsRepository.GetSettingAsync<LightningTerminalSettings>()
                       ?? new LightningTerminalSettings();
        settings.UiPassword = null;
        await settingsRepository.UpdateSetting(settings);

        TempData[WellKnownTempData.SuccessMessage] = "Password forgotten.";
        return RedirectToAction(nameof(Settings));
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
