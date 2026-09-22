using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Configuration;
using BTCPayServer.Plugins.LightningTerminal.Services;
using BTCPayServer.Plugins.LightningTerminal.ViewModels;
using Grpc.Core;
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
    BTCPayServerOptions serverOptions) : Controller
{
    /// <summary>
    /// How long a pairing session stays valid. Matches litcli's own default, so a session minted here
    /// behaves the same as one minted with `litcli sessions add`.
    /// </summary>
    private static readonly TimeSpan SessionExpiry = TimeSpan.FromDays(90);

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
            DockerDeployment = serverOptions.DockerDeployment
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
            TerminalOperation.Install when status.Installed => new InstallBlocker(
                "Lightning Terminal is already installed on this deployment."),
            TerminalOperation.Install when status.UpstreamOnly => new InstallBlocker(
                "This deployment already runs Lightning Terminal through BTCPay's own fragment. Adding this " +
                "plugin's fragment as well would leave two definitions of the same container, so switch " +
                "instead of installing."),
            TerminalOperation.Install => status.Backend.Blocker,
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
                TerminalOperation.Install => LitdFragment.InstallCommand,
                TerminalOperation.Switch => LitdFragment.SwitchCommand,
                TerminalOperation.Uninstall => LitdFragment.UninstallCommand,
                TerminalOperation.Wipe => LitdFragment.WipeCommand,
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            },
            DockerDeployment = serverOptions.DockerDeployment,
            Blocker = blocker
        });
    }

    /// <summary>
    /// Mints a fresh admin LNC session and hands back the link that pairs Terminal on the web with it.
    /// </summary>
    /// <remarks>
    /// A POST, and always a new session: a pairing phrase is single-use, so a GET that minted one
    /// would burn a credential on every refresh, prefetch or link preview.
    /// </remarks>
    [HttpPost("connect")]
    public async Task<IActionResult> Connect(CancellationToken cancellationToken)
    {
        var label = $"BTCPay Server {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC";
        try
        {
            var session = await client.AddSessionAsync(label, SessionExpiry, cancellationToken);
            return View(new TerminalConnectViewModel
            {
                PairingUrl = TerminalConnect.PairingUrl(session),
                PairingPhrase = session.PairingSecretMnemonic,
                MailboxServerAddress = session.MailboxServerAddr,
                Label = session.Label,
                Expiry = DateTimeOffset.FromUnixTimeSeconds((long)session.ExpiryTimestampSeconds)
            });
        }
        catch (Exception ex) when (ex is RpcException or LitdNotReadyException)
        {
            TempData[WellKnownTempData.ErrorMessage] =
                $"Could not create a pairing session: {(ex is RpcException rpc ? LitdClient.Explain(rpc) : ex.Message)}";
            return RedirectToAction(nameof(Index));
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
