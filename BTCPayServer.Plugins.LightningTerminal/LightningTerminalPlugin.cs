using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.LightningTerminal.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.LightningTerminal;

public class LightningTerminalPlugin : BaseBTCPayServerPlugin
{
    /// <summary>
    /// Ties the nav entry to the pages it links to: the nav partial stamps it as this item's id, and
    /// every page announces it through <c>LayoutModel</c> so the entry renders active. The two have to
    /// agree or the entry simply never highlights, which nothing else would catch.
    /// </summary>
    /// <remarks>
    /// It is also the DOM id BTCPay writes (<c>menu-item-LightningTerminal</c>), which is why it carries
    /// the full plugin name rather than a short one - the Plugins menu is shared with every other plugin.
    /// </remarks>
    public const string MenuItemId = "LightningTerminal";

    /// <summary>
    /// Partial rendered into the <c>header-nav</c> extension point. Resolved by name against
    /// <c>/Views/Shared/{name}.cshtml</c>, and that search runs across every loaded plugin, so the
    /// directory segment is what keeps it from colliding with another plugin's nav partial.
    /// </summary>
    public const string NavExtensionPartial = $"{MenuItemId}/NavExtension";

    /// <summary>
    /// Partial that renders an <see cref="Services.InstallBlocker"/>. Shared by the landing screen and
    /// the instructions page, and named here for the same reason as the nav partial: partial lookup
    /// searches /Views/Shared across every loaded plugin, so the directory segment is what keeps it ours.
    /// </summary>
    public const string BlockerPartial = $"{MenuItemId}/_Blocker";

    /// <summary>
    /// Partial that renders one pasteable host command with its copy button. Every such block on this
    /// plugin goes through it, so they cannot drift apart in presentation.
    /// </summary>
    public const string HostCommandPartial = $"{MenuItemId}/_HostCommand";

    public override string Identifier => "BTCPayServer.Plugins.LightningTerminal";
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
        services.AddSingleton<LitdFragment>();
        services.AddSingleton<LightningBackendDetector>();
        services.AddSingleton<LitdClient>();
        services.AddSingleton<LitdStatusService>();

        // header-nav is the "Plugins" section of the main navigation; the partial hides itself from
        // anyone without CanModifyServerSettings.
        services.AddUIExtension("header-nav", NavExtensionPartial);

        base.Execute(services);
    }
}
