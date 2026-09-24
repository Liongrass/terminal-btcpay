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

    /// <summary>
    /// The format a date is typed and posted in: ISO, unambiguous, and the same thing
    /// <c>&lt;input type="date"&gt;</c> would have submitted.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="DateFormat"/>. Read-only dates carry the plugin's house style;
    /// a value that travels over the wire and gets parsed again should be ISO 8601.
    /// </remarks>
    public const string InputDateFormat = "yyyy-MM-dd";

    /// <summary>
    /// <see cref="InputDateFormat"/> in flatpickr's token language, for its <c>dateFormat</c> option.
    /// </summary>
    /// <remarks>
    /// The two have to say the same thing: flatpickr writes the input's value in this format and the
    /// server parses it with the other, so a mismatch means every date is rejected.
    /// </remarks>
    public const string InputDateFormatForFlatpickr = "Y-m-d";

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
