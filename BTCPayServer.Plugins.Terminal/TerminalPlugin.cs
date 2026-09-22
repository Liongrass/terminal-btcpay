using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.Terminal.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.Terminal;

public class TerminalPlugin : BaseBTCPayServerPlugin
{
    public override string Identifier => "BTCPayServer.Plugins.Terminal";
    public override string Name => "Lightning Terminal";

    public override string Description =>
        "Install and manage Lightning Terminal (litd) on a BTCPay Server Docker deployment backed by the " +
        "bundled LND, and pair it with Terminal on the web over Lightning Node Connect.";

    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        // 2.4.4 is where core replaced its SSH settings with the btcpay-host integration and moved
        // maintenance into its own plugin. This plugin reads deployment facts that only exist in that
        // shape, and its install instructions assume btcpay-fragments is present on the host.
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.4.4" }
    ];

    public override void Execute(IServiceCollection services)
    {
        services.AddSingleton<TerminalOptions>();
        services.AddSingleton<LitdPaths>();
        services.AddSingleton<LightningBackendDetector>();
        services.AddSingleton<LitdClient>();
        services.AddSingleton<LitdStatusService>();

        // header-nav is the "Plugins" section of the main navigation; the partial hides itself from
        // anyone without CanModifyServerSettings.
        services.AddUIExtension("header-nav", "Terminal/NavExtension");

        base.Execute(services);
    }
}
