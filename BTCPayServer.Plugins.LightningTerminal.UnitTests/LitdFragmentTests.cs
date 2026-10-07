using BTCPayServer.Plugins.LightningTerminal.Services;
using Xunit;

namespace BTCPayServer.Plugins.LightningTerminal.UnitTests;

/// <summary>
/// The commands this plugin asks an operator to paste into a root shell. Nothing here runs them, so
/// what is checked is the shape an operator is handed: the right fragment, the right order, and the
/// guards that stop a paste doing damage on a host it was not meant for.
/// </summary>
public class LitdFragmentTests
{
    private static readonly LitdFragment Fragment = new();

    public static TheoryData<string> EveryCommand =>
    [
        Fragment.InstallCommand,
        Fragment.UpdateCommand,
        Fragment.UninstallCommand,
        Fragment.WipeCommand
    ];

    [Theory]
    [MemberData(nameof(EveryCommand))]
    public void EveryCommandIsAFailFastSubshell(string command)
    {
        // These are pasted into a root shell. An unset BTCPAY_BASE_DIRECTORY has to stop the block
        // rather than resolve to a path under /, and a failure must not close the operator's session
        // the way a bare exit would.
        Assert.StartsWith("(\nset -eu\n", command, StringComparison.Ordinal);
        Assert.EndsWith("\n)", command, StringComparison.Ordinal);
        Assert.Contains(". /etc/profile.d/btcpay-env.sh", command, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallSelectsBtcpaysOwnFragment()
    {
        // No custom fragment is written any more: the upstream one already runs litd headless and
        // mounts its data volume here, so there is nothing left for this plugin to generate.
        Assert.Contains($"btcpay-fragments add {TerminalOptions.FragmentName}", Fragment.InstallCommand);
        Assert.DoesNotContain("<<'LITD_FRAGMENT'", Fragment.InstallCommand);
        Assert.DoesNotContain(".custom", Fragment.InstallCommand);
    }

    [Fact]
    public void UpdateRefreshesBtcpayRatherThanTouchingFragments()
    {
        // The one answer to a litd installed by a superseded fragment: updating replaces it, and the
        // data volume is declared under the same name by both, so nothing is lost.
        Assert.Contains("btcpay-update.sh", Fragment.UpdateCommand);
        Assert.DoesNotContain("btcpay-fragments", Fragment.UpdateCommand);
        Assert.DoesNotContain("docker volume rm", Fragment.UpdateCommand);
    }

    [Fact]
    public void UninstallKeepsTheDataVolume()
    {
        Assert.Contains($"btcpay-fragments remove {TerminalOptions.FragmentName}", Fragment.UninstallCommand);
        Assert.DoesNotContain("docker volume rm", Fragment.UninstallCommand);
        Assert.DoesNotContain("rm -f", Fragment.UninstallCommand);
    }

    [Fact]
    public void WipeRemovesTheContainerBeforeTheVolumeAndBringsLitdBack()
    {
        // A volume cannot be removed while a container is attached to it. litd then has to come back:
        // this operation is about the data, and leaving the install alone is what Uninstall is for.
        var wipe = Fragment.WipeCommand;

        Assert.True(
            wipe.IndexOf($"docker rm -f {TerminalOptions.ContainerName}", StringComparison.Ordinal) <
            wipe.IndexOf("docker volume rm", StringComparison.Ordinal));
        Assert.Contains("btcpay-up.sh", wipe);
        Assert.DoesNotContain("btcpay-fragments", wipe);
    }

    [Fact]
    public void WipeMatchesTheVolumeUnderItsComposeProjectPrefix()
    {
        // Compose names the volume <project>_lnd_lit_datadir, so a literal `docker volume rm
        // lnd_lit_datadir` would miss it on every real deployment.
        Assert.Contains($"(^|_){TerminalOptions.DataVolumeName}$", Fragment.WipeCommand, StringComparison.Ordinal);
    }
}
