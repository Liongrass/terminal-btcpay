using System.Globalization;
using BTCPayServer.Plugins.LightningTerminal.Services;
using Xunit;

namespace BTCPayServer.Plugins.LightningTerminal.UnitTests;

/// <summary>
/// One date format for every date on these pages, and it has to survive the request's culture.
/// </summary>
public class TerminalDatesTests
{
    private static readonly DateTimeOffset Moment =
        new(2026, 9, 23, 17, 32, 17, TimeSpan.Zero);

    [Fact]
    public void DatesAreSlashedYearFirst()
    {
        Assert.Equal("2026/09/23 UTC", Moment.ToDisplayDate());
        Assert.Equal("2026/09/23 17:32 UTC", Moment.ToDisplayDateTime());
        Assert.Equal("2026/09/23", Moment.ToBareDate());
    }

    [Theory]
    [InlineData("de-DE")]  // date separator "."
    [InlineData("fi-FI")]  // date separator "."
    [InlineData("en-US")]
    [InlineData("ar-SA")]  // a non-Gregorian default calendar
    public void TheSeparatorSurvivesTheRequestCulture(string culture)
    {
        // An unquoted / in a .NET custom format is a placeholder for the culture's DateSeparator, not
        // a slash - "yyyy/MM/dd" renders 2026.09.23 under de-DE. BTCPay serves whatever culture the
        // request carries, so this would change shape per visitor if the format were not pinned.
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);

            Assert.Equal("2026/09/23 UTC", Moment.ToDisplayDate());
            Assert.Equal("2026/09/23 17:32 UTC", Moment.ToDisplayDateTime());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void AMomentIsRenderedInUtcNotItsOwnOffset()
    {
        // litd hands back unix timestamps and the deployment's clock is not the reader's, so an
        // offset that leaked through would move a date across midnight for half the world.
        var tokyo = new DateTimeOffset(2026, 9, 24, 2, 32, 0, TimeSpan.FromHours(9));

        Assert.Equal("2026/09/23 17:32 UTC", tokyo.ToDisplayDateTime());
    }

    [Fact]
    public void TheSessionLabelUsesTheSameFormat()
    {
        Assert.Equal("Lightning Terminal 2026/09/23", TerminalConnect.DefaultSessionLabel(Moment));
    }
}
