using System.Globalization;

namespace BTCPayServer.Plugins.LightningTerminal.Services;

/// <summary>
/// One date format for every date these pages show.
/// </summary>
/// <remarks>
/// <para>
/// Fixing the culture to invariant is what makes the shape stable, and it is not optional. An unquoted
/// <c>/</c> in a .NET custom format string is not a slash - it is a placeholder for the current
/// culture's <see cref="DateTimeFormatInfo.DateSeparator"/>. Formatted against the request's culture,
/// <c>yyyy/MM/dd</c> renders <c>2026.09.23</c> under de-DE and fi-FI, and under ar-SA it comes back as
/// <c>2026‏/09‏/23</c> - right-to-left marks wedged between the parts, invisible until
/// something downstream chokes on them. BTCPay serves whatever culture the request carries.
/// </para>
/// <para>
/// The separators are quoted as well. That is belt and braces rather than the working guard - with the
/// culture pinned they render the same either way - but it keeps the format honest if anyone later
/// formats it without passing a culture.
/// </para>
/// <para>
/// Everything is rendered in UTC and says so. litd hands back unix timestamps, the deployment's clock
/// is not the reader's, and an expiry that silently shifted by a timezone would be worse than a longer
/// label.
/// </para>
/// </remarks>
public static class TerminalDates
{
    public const string DateFormat = "yyyy'/'MM'/'dd";
    public const string DateTimeFormat = "yyyy'/'MM'/'dd HH':'mm";

    /// <summary>A calendar date, e.g. <c>2026/09/23 UTC</c>.</summary>
    public static string ToDisplayDate(this DateTimeOffset value) =>
        $"{value.UtcDateTime.ToString(DateFormat, CultureInfo.InvariantCulture)} UTC";

    /// <summary>A date and time to the minute, e.g. <c>2026/09/23 17:32 UTC</c>.</summary>
    public static string ToDisplayDateTime(this DateTimeOffset value) =>
        $"{value.UtcDateTime.ToString(DateTimeFormat, CultureInfo.InvariantCulture)} UTC";

    /// <summary>The bare date, with no UTC suffix, for somewhere a sentence already carries the context.</summary>
    public static string ToBareDate(this DateTimeOffset value) =>
        value.UtcDateTime.ToString(DateFormat, CultureInfo.InvariantCulture);
}
