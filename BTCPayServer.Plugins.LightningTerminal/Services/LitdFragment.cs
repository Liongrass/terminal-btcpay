namespace BTCPayServer.Plugins.LightningTerminal.Services;

/// <summary>
/// The shell commands this plugin asks an operator to paste into a root shell on the BTCPay Server
/// host, to add litd, remove it, or destroy its data.
/// </summary>
/// <remarks>
/// <para>
/// litd is installed by selecting btcpayserver-docker's own Lightning Terminal fragment, which already
/// runs litd headless and mounts its data volume into the BTCPay Server container - everything this
/// plugin needs to report status, mint pairing sessions and read logs. Nothing is generated here, so
/// there is no custom fragment to write, to keep in step with upstream, or to leave behind.
/// </para>
/// <para>
/// They are instructions rather than actions because BTCPay Server 2.4.4 removed the plugin's route to
/// the host: <c>btcpay-host</c> exposes a fixed whitelist that fragment management is not on. See
/// README.md.
/// </para>
/// <para>
/// Every command runs in a <c>( set -eu )</c> subshell. These are pasted into a root shell, so an
/// unset <c>BTCPAY_BASE_DIRECTORY</c> - on a host that is not a btcpayserver-docker deployment - has
/// to stop the block rather than resolve to a path under <c>/</c>. A subshell also means a failure
/// never closes the operator's session the way a bare <c>exit</c> would.
/// </para>
/// </remarks>
public class LitdFragment
{
    /// <summary>
    /// Selects the fragment. <c>btcpay-fragments</c> regenerates the Compose file and brings the stack
    /// back up as it goes, so BTCPay Server itself restarts partway through.
    /// </summary>
    public string InstallCommand =>
        $"""
        (
        set -eu
        . /etc/profile.d/btcpay-env.sh
        btcpay-fragments add {TerminalOptions.FragmentName}
        )
        """;

    /// <summary>
    /// Updates btcpayserver-docker itself, which is how a deployment running a superseded Lightning
    /// Terminal fragment gets the current one. litd's data volume is declared under the same name by
    /// both, so accounts, sessions and history carry over.
    /// </summary>
    public string UpdateCommand =>
        """
        (
        set -eu
        . /etc/profile.d/btcpay-env.sh
        btcpay-update.sh
        )
        """;

    /// <summary>
    /// Deselects the fragment and regenerates the stack without litd. litd's data volume is untouched:
    /// nothing here removes a named Compose volume, so the macaroon, accounts, sessions and
    /// Loop/Pool/Faraday records all survive, and a later re-install picks them straight back up.
    /// </summary>
    public string UninstallCommand =>
        $"""
        (
        set -eu
        . /etc/profile.d/btcpay-env.sh
        btcpay-fragments remove {TerminalOptions.FragmentName}
        )
        """;

    /// <summary>
    /// Destroys litd's data volume and brings litd back empty. Irreversible: the macaroon, every LNC
    /// session, every Loop/Pool/Faraday record and litd's own database go with it. LND's own data is in
    /// a separate volume and is not touched.
    /// </summary>
    /// <remarks>
    /// Deliberately leaves the fragment selected. The container is removed rather than deselected
    /// because a volume cannot be removed while a container is attached to it, and <c>btcpay-up.sh</c>
    /// then recreates litd from the same fragment with an empty volume. That keeps this one operation
    /// about the data, leaves installation to Uninstall, and avoids rebuilding the whole stack twice.
    /// </remarks>
    public string WipeCommand =>
        $$"""
        (
        set -eu
        . /etc/profile.d/btcpay-env.sh
        docker rm -f {{TerminalOptions.ContainerName}}
        volume="$(docker volume ls -q | grep -E '(^|_){{TerminalOptions.DataVolumeName}}$' || true)"
        if [ -n "$volume" ]; then
            docker volume rm "$volume"
        else
            echo "No {{TerminalOptions.DataVolumeName}} volume found - nothing left to wipe."
        fi
        btcpay-up.sh
        )
        """;
}
