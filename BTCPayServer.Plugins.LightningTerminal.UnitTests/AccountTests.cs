using System.Globalization;
using BTCPayServer.Plugins.LightningTerminal.Services;
using BTCPayServer.Plugins.LightningTerminal.ViewModels;
using Xunit;

namespace BTCPayServer.Plugins.LightningTerminal.UnitTests;

/// <summary>
/// The account form's two translations into litd's terms, and how an account reads back out.
/// </summary>
public class AccountRulesTests
{
    [Theory]
    [InlineData("0123456789abcdef")]  // exactly an id's length, all hex
    [InlineData("DEADBEEFDEADBEEF")]  // case makes no difference to litd's check
    public void LabelsThatCouldBeReadAsAnIdAreRejected(string label) =>
        Assert.True(AccountRules.LooksLikeAnAccountId(label));

    [Theory]
    [InlineData("Shop till")]
    [InlineData("0123456789abcde")]   // one short
    [InlineData("0123456789abcdef0")] // one long
    [InlineData("0123456789abcdeg")]  // right length, but g is not hex
    [InlineData("")]
    [InlineData(null)]
    public void EverythingElseIsAllowed(string? label) =>
        Assert.False(AccountRules.LooksLikeAnAccountId(label));

    [Fact]
    public void AnEmptyExpiryMeansTheAccountNeverExpires()
    {
        // litd's zero, and the form's default.
        foreach (var blank in new[] { null, "", "   " })
        {
            Assert.True(AccountRules.TryParseExpiry(blank, out var expiry));
            Assert.Null(expiry);
        }
    }

    [Fact]
    public void AnExpiryIsReadInTheFormatTheFormPosts()
    {
        Assert.True(AccountRules.TryParseExpiry("2026-09-30", out var expiry));
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 23, 59, 59, TimeSpan.Zero).ToUnixTimeSeconds(),
            expiry!.Value.ToUnixTimeSeconds());
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("en-US")]
    [InlineData("ar-SA")]
    public void TheExpiryIsReadTheSameWayUnderEveryCulture(string culture)
    {
        // Parsed exactly against the invariant culture rather than left to model binding. Under en-US
        // a bound DateTime would read 09/10 as September; under de-DE, as October. Same string, two
        // different accounts, no error either way.
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);

            Assert.True(AccountRules.TryParseExpiry("2026-09-10", out var expiry));
            Assert.Equal(9, expiry!.Value.UtcDateTime.Month);
            Assert.Equal(10, expiry.Value.UtcDateTime.Day);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("30/09/2026")]   // day first
    [InlineData("09/30/2026")]   // month first
    [InlineData("2026/09/30")]   // the display format, which is not the input format
    [InlineData("2026-13-01")]   // no such month
    [InlineData("2026-02-30")]   // no such day
    [InlineData("tomorrow")]
    public void AnythingElseIsRejectedRatherThanGuessedAt(string value)
    {
        // Better a form error than an account that quietly expires on a date nobody chose.
        Assert.False(AccountRules.TryParseExpiry(value, out var expiry));
        Assert.Null(expiry);
    }

    [Fact]
    public void ThePickerAndTheParserAgreeOnTheFormat()
    {
        // flatpickr writes the input's value with its own token language and the server reads it with
        // .NET's. If these drift apart every date is rejected, and only at run time.
        Assert.Equal("yyyy-MM-dd", TerminalDates.InputDateFormat);
        Assert.Equal("Y-m-d", TerminalDates.InputDateFormatForFlatpickr);
    }

    [Fact]
    public void ExpiringOnADateMeansTheEndOfThatDay()
    {
        // Not its start: an account set to expire "on the 30th" should work through the 30th, and
        // midnight would cut it a day short.
        var expiry = AccountRules.EndOfDayUtc(new DateTime(2026, 9, 30));

        Assert.Equal(TimeSpan.Zero, expiry.Offset);
        Assert.Equal(new DateTime(2026, 9, 30, 23, 59, 59), expiry.UtcDateTime.AddTicks(1).AddSeconds(-1));
        // litd truncates to whole seconds, so this is what it actually stores.
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 23, 59, 59, TimeSpan.Zero).ToUnixTimeSeconds(),
            expiry.ToUnixTimeSeconds());
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    public void TheDateIsTakenAsUtcWhateverKindTheBinderAttached(DateTimeKind kind)
    {
        // A picker gives a calendar date, not an instant, but the binder chooses the kind. Local used
        // to throw here on any server not running on UTC - an unhandled 500 rather than a form error.
        var date = DateTime.SpecifyKind(new DateTime(2026, 9, 30), kind);

        var expiry = AccountRules.EndOfDayUtc(date);

        Assert.Equal(TimeSpan.Zero, expiry.Offset);
        Assert.Equal(30, expiry.UtcDateTime.Day);
        Assert.Equal(9, expiry.UtcDateTime.Month);
    }
}

public class AccountViewModelTests
{
    private static AccountViewModel Account(string label, DateTimeOffset? expiry = null) =>
        new("0011223344556677", label, 100_000, 42_000, expiry);

    [Fact]
    public void AnUnlabelledAccountIsShownByItsId()
    {
        // A label is optional in litd, so the id is the only handle every account is sure to have.
        Assert.Equal("0011223344556677", Account("").Display);
        Assert.Equal("Shop till", Account("Shop till").Display);
    }

    [Fact]
    public void AnAccountWithNoExpiryNeverExpires()
    {
        Assert.False(Account("Shop till").HasExpired);
    }

    [Fact]
    public void ExpiryIsComparedAgainstNow()
    {
        Assert.True(Account("Old", DateTimeOffset.UtcNow.AddDays(-1)).HasExpired);
        Assert.False(Account("Live", DateTimeOffset.UtcNow.AddDays(1)).HasExpired);
    }
}
