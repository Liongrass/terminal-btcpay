using System.Reflection;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Plugins.LightningTerminal.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Razor.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BTCPayServer.Plugins.LightningTerminal.UnitTests;

/// <summary>
/// Pins the names BTCPay resolves by convention rather than by reference.
/// </summary>
/// <remarks>
/// Renaming a controller or moving a view directory compiles perfectly and then fails at runtime,
/// because MVC finds views by path and BTCPay searches those paths across every loaded plugin - which
/// is how this plugin collided with another one called Terminal in the first place. Nothing else in
/// the build checks that a controller and its view directory still agree.
/// </remarks>
public class PluginConventionsTests
{
    private static readonly Assembly PluginAssembly = typeof(LightningTerminalPlugin).Assembly;

    /// <summary>Paths of every Razor view compiled into the plugin, e.g. /Views/UILightningTerminal/Index.cshtml.</summary>
    private static readonly string[] CompiledViews = PluginAssembly
        .GetCustomAttributes<RazorCompiledItemAttribute>()
        .Select(item => item.Identifier)
        .ToArray();

    private static string ViewDirectoryFor<TController>() where TController : Controller
    {
        var name = typeof(TController).Name;
        // MVC derives the view directory from the controller name with "Controller" trimmed.
        return $"/Views/{name[..^"Controller".Length]}/";
    }

    [Fact]
    public void ThereAreCompiledViewsAtAll()
    {
        // Everything below is vacuously true if the Razor SDK stopped compiling views into the
        // assembly, which would make this whole file a silent no-op.
        Assert.NotEmpty(CompiledViews);
    }

    [Theory]
    [InlineData(nameof(UILightningTerminalController.Index))]
    [InlineData(nameof(UILightningTerminalController.Instructions))]
    [InlineData(nameof(UILightningTerminalController.Connect))]
    public void EveryViewReturningActionHasAViewUnderItsControllerDirectory(string action)
    {
        var expected = $"{ViewDirectoryFor<UILightningTerminalController>()}{action}.cshtml";

        Assert.Contains(expected, CompiledViews);
    }

    [Fact]
    public void NoViewIsStrandedOutsideTheControllerAndSharedDirectories()
    {
        // Catches the other half of a rename: a view directory moved while the controller stayed, or
        // a leftover copy of a view at the old path still shipping in the package.
        var allowedPrefixes = new[] { ViewDirectoryFor<UILightningTerminalController>(), "/Views/Shared/" };
        var stranded = CompiledViews
            .Where(view => !view.StartsWith("/Views/_View", StringComparison.Ordinal))
            .Where(view => !allowedPrefixes.Any(prefix => view.StartsWith(prefix, StringComparison.Ordinal)))
            .ToArray();

        Assert.Empty(stranded);
    }

    [Fact]
    public void EveryRegisteredUiExtensionResolvesToASharedView()
    {
        var services = new ServiceCollection();
        new LightningTerminalPlugin().Execute(services);

        var extensions = services
            .Where(descriptor => descriptor.ServiceType == typeof(IUIExtension))
            .Select(descriptor => (IUIExtension)descriptor.ImplementationInstance!)
            .ToArray();

        Assert.NotEmpty(extensions);
        foreach (var extension in extensions)
        {
            // BTCPay renders these with Html.RenderPartialAsync from whichever controller happens to be
            // serving the page, so /Views/Shared is the only directory that always resolves.
            Assert.Contains($"/Views/Shared/{extension.Partial}.cshtml", CompiledViews);
        }
    }

    [Fact]
    public void EveryPartialNamedOnThePluginResolvesToACompiledSharedView()
    {
        // Reflective rather than a list, so a partial added later is covered without anyone
        // remembering to come back here. A <partial name="..."> that resolves to nothing throws only
        // when the page is actually opened, and only on the branch that renders it.
        var partials = typeof(LightningTerminalPlugin)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
            .Where(field => field.Name.EndsWith("Partial", StringComparison.Ordinal))
            .Select(field => (field.Name, Value: (string)field.GetRawConstantValue()!))
            .ToArray();

        Assert.NotEmpty(partials);
        foreach (var (name, value) in partials)
        {
            Assert.True(
                CompiledViews.Contains($"/Views/Shared/{value}.cshtml"),
                $"{name} is \"{value}\" but /Views/Shared/{value}.cshtml is not compiled into the plugin");
        }
    }

    [Fact]
    public void NavPartialIsRegisteredIntoThePluginsMenu()
    {
        var services = new ServiceCollection();
        new LightningTerminalPlugin().Execute(services);

        var nav = services
            .Where(descriptor => descriptor.ServiceType == typeof(IUIExtension))
            .Select(descriptor => (IUIExtension)descriptor.ImplementationInstance!)
            .Single(extension => extension.Partial == LightningTerminalPlugin.NavExtensionPartial);

        // header-nav is the "Plugins" accordion of the main navigation, and it is the only extension
        // point that renders with no store selected - which this plugin needs, being server-wide.
        Assert.Equal("header-nav", nav.Location);
    }

    [Fact]
    public void IdentifierMatchesTheAssemblyName()
    {
        // BTCPay keys installed plugins by Identifier and PluginPacker names the output directory after
        // the assembly. Letting them drift would orphan every existing install on the next release.
        Assert.Equal(PluginAssembly.GetName().Name, new LightningTerminalPlugin().Identifier);
    }

    [Fact]
    public void VersionSurvivesBeingBuiltFromAGitCheckout()
    {
        // BaseBTCPayServerPlugin parses AssemblyInformationalVersion with Version.TryParse. The SDK
        // appends "+<commit sha>" to that attribute in a git checkout unless
        // IncludeSourceRevisionInInformationalVersion is off, and the silent fallback to AssemblyVersion
        // changes the version BTCPay advertises and the directory the package lands in.
        var informational = PluginAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        Assert.NotNull(informational);
        Assert.DoesNotContain('+', informational);
        Assert.True(Version.TryParse(informational, out _), $"'{informational}' is not a parseable Version");
    }
}
