using BTCPayServer.Plugins.LightningTerminal.Services;

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
public enum TerminalOperation
{
    Install,

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
    public string? Blocker { get; init; }
}

public class TerminalConnectViewModel
{
    public required string PairingUrl { get; init; }
    public required string PairingPhrase { get; init; }
    public required string MailboxServerAddress { get; init; }
    public required string Label { get; init; }
    public required DateTimeOffset Expiry { get; init; }
}
