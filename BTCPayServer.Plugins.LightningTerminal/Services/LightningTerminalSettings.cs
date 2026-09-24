namespace BTCPayServer.Plugins.LightningTerminal.Services;

/// <summary>Server-level settings for this plugin.</summary>
public class LightningTerminalSettings
{
    /// <summary>
    /// litd's web UI password, needed only when litd runs from btcpayserver-docker's own fragment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// That fragment does not mount litd's data directory into the BTCPay container, so there is no
    /// macaroon to read. litd's proxy accepts HTTP basic auth instead when its UI is enabled and
    /// resolves the right macaroon itself, so this password stands in for the file.
    /// </para>
    /// <para>
    /// Stored as written, which is what BTCPay does with the SMTP password in EmailSettings. It is a
    /// node credential, so it is only ever asked for on a deployment that already keeps it in
    /// <c>secrets/lit_password</c> on the host - an install from this plugin's own fragment needs no
    /// password at all, because it turns the UI off.
    /// </para>
    /// </remarks>
    public string? UiPassword { get; set; }
}
