using System.ComponentModel.DataAnnotations;
using BTCPayServer.Plugins.LightningTerminal.Services;
using Litrpc;

namespace BTCPayServer.Plugins.LightningTerminal.ViewModels;

public class TerminalIndexViewModel
{
    public required LitdStatus Status { get; init; }

    /// <summary>
    /// Existing LNC sessions, newest first. Empty and <see cref="SessionsError"/> set when litd is
    /// reachable but would not list them.
    /// </summary>
    public IReadOnlyList<TerminalSessionViewModel> Sessions { get; init; } = [];

    public string? SessionsError { get; init; }

    /// <summary>
    /// False when BTCPay is not running from a btcpayserver-docker deployment, in which case the
    /// install instructions do not apply and the page says so instead of offering them.
    /// </summary>
    public required bool DockerDeployment { get; init; }

    /// <summary>
    /// The host commands that install litd. Built by the controller rather than reached for from the
    /// view, because the fragment now depends on the deployment's network - see LitdFragment.
    /// </summary>
    public required string InstallCommand { get; init; }

    /// <summary>litd's accounts. Empty when it has none, or when listing them failed.</summary>
    public IReadOnlyList<AccountViewModel> Accounts { get; init; } = [];

    public string? AccountsError { get; init; }
}

public class TerminalSessionViewModel
{
    public required string Label { get; init; }
    public required string State { get; init; }
    public required string Type { get; init; }
    public required DateTimeOffset Expiry { get; init; }
    public required string LocalPublicKey { get; init; }

    /// <summary>True once a client has paired with this session, so the pairing phrase is spent.</summary>
    public required bool Paired { get; init; }

    /// <summary>True while the session can still be used - neither revoked nor expired.</summary>
    public required bool Active { get; init; }

    /// <summary>
    /// A link that pairs Terminal on the web with this session, present only while the session is
    /// active and unpaired. A spent or dead phrase would be a link that silently does nothing.
    /// </summary>
    public string? PairingUrl { get; init; }
}

/// <summary>Which host-side operation the instructions page is walking the operator through.</summary>
/// <remarks>
/// No Install member: installing is rendered inline on the landing screen rather than on a page of its
/// own, so there is no route that would reach it here. These three all act on an existing install.
/// </remarks>
public enum TerminalOperation
{
    /// <summary>Move from btcpayserver-docker's own Lightning Terminal fragment to this plugin's headless one.</summary>
    Switch,
    Uninstall,
    Wipe
}

public class TerminalInstructionsViewModel
{
    public required TerminalOperation Operation { get; init; }
    public required string Command { get; init; }
    public required bool DockerDeployment { get; init; }

    /// <summary>
    /// Set when the operation cannot sensibly be run right now - litd is not installed and so cannot
    /// be uninstalled, or the deployment has no LND for litd to attach to.
    /// </summary>
    public InstallBlocker? Blocker { get; init; }
}

/// <summary>
/// The manual "generate a pairing phrase" form. Everything litcli's `sessions add` takes, minus the
/// flags that need a permissions editor to be useful.
/// </summary>
public class NewSessionViewModel
{
    [Required]
    [MaxLength(200)]
    [Display(Name = "Label")]
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Days rather than litcli's raw seconds - an expiry is a human decision, and nobody wants to type
    /// 7776000. Capped at ten years because litd stores it as an absolute timestamp.
    /// </summary>
    [Range(1, 3650)]
    [Display(Name = "Expires in (days)")]
    public int ExpiryDays { get; set; } = 90;

    [Required]
    [Display(Name = "Mailbox server")]
    public string MailboxServer { get; set; } = LitdClient.DefaultMailboxServer;

    [Display(Name = "Type")]
    public SessionType Type { get; set; } = SessionType.TypeMacaroonAdmin;

    /// <summary>
    /// Only meaningful for an account session, and required for one - litd rejects the type without a
    /// parseable id. Empty for every other type.
    /// </summary>
    [Display(Name = "Account")]
    public string? AccountId { get; set; }

    /// <summary>
    /// The accounts litd reported, for the dropdown. Empty when litd has none, which is the normal
    /// case for a node that has never used them.
    /// </summary>
    public IReadOnlyList<AccountOption> Accounts { get; set; } = [];
}

/// <param name="Id">litd's account id, which is what the session is actually scoped to.</param>
/// <param name="Description">Label and id together, since a label alone need not be unique.</param>
public record AccountOption(string Id, string Description);

/// <summary>A freshly minted session, shown once so the phrase can be copied or used.</summary>
public class SessionCreatedViewModel
{
    public required string Label { get; init; }
    public required string Type { get; init; }
    public required DateTimeOffset Expiry { get; init; }
    public required string MailboxServer { get; init; }
    public required string PairingPhrase { get; init; }
    public required string PairingUrl { get; init; }
}

/// <summary>One of litd's accounts as shown in the list on the plugin's landing screen.</summary>
/// <param name="Id">litd's hex account id, which is how every other call addresses it.</param>
/// <param name="Label">Optional and, when set, unique across accounts. Empty for an unlabelled one.</param>
/// <param name="InitialBalanceSats">The ceiling it was created with.</param>
/// <param name="CurrentBalanceSats">
/// What is left to spend. Signed, because litd tracks it that way - a payment reserves its fee limit
/// up front, so a balance can briefly read lower than the amount actually settled.
/// </param>
/// <param name="Expiry">When it stops working, or null for an account that never expires.</param>
public record AccountViewModel(
    string Id,
    string Label,
    ulong InitialBalanceSats,
    long CurrentBalanceSats,
    DateTimeOffset? Expiry)
{
    public string Display => string.IsNullOrWhiteSpace(Label) ? Id : Label;

    public bool HasExpired => Expiry is { } expiry && expiry <= DateTimeOffset.UtcNow;
}

public class NewAccountViewModel
{
    /// <summary>
    /// Optional, unique when set, and litd refuses one that could be mistaken for an account id -
    /// sixteen hex characters exactly.
    /// </summary>
    [MaxLength(200)]
    [Display(Name = "Label")]
    public string? Label { get; set; }

    [Range(0, 21_000_000_00000000)]
    [Display(Name = "Starting balance (sats)")]
    public long BalanceSats { get; set; }

    /// <summary>
    /// Null for an account that never expires, which is litd's zero and this form's default.
    /// </summary>
    [DataType(DataType.Date)]
    [Display(Name = "Expires on")]
    public DateTime? Expiry { get; set; }
}

/// <param name="Hash">lnd's payment hash, hex.</param>
/// <param name="State">Whatever lnd calls it - litd passes the string through untouched.</param>
/// <param name="ReservedSats">
/// What the payment set aside, fee limit included. Deliberately not called "amount": litd's own
/// comment notes the actual debit is usually lower, so presenting it as the sum paid would overstate it.
/// </param>
public record AccountPaymentViewModel(string Hash, string State, long ReservedSats);

public class AccountDetailViewModel
{
    public required AccountViewModel Account { get; init; }
    public required DateTimeOffset LastUpdate { get; init; }

    /// <summary>Payment hashes only - litd records nothing else about an account's invoices.</summary>
    public IReadOnlyList<string> InvoiceHashes { get; init; } = [];

    public IReadOnlyList<AccountPaymentViewModel> Payments { get; init; } = [];

    /// <summary>
    /// The account's macaroon, hex encoded, once the operator has asked for it. Null the rest of the
    /// time - it is re-derived on request and never stored, so the page does not carry it by default.
    /// </summary>
    public string? Macaroon { get; init; }
}
