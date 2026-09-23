namespace BTCPayServer.Plugins.LightningTerminal.Services;

/// <summary>
/// Turns what the account form collects into what litd's Accounts service expects, and applies the one
/// rule litd enforces that a form can check first.
/// </summary>
public static class AccountRules
{
    /// <summary>
    /// Hex characters in an account id - litd's <c>AccountIDLen</c> of 8 bytes.
    /// </summary>
    public const int AccountIdHexLength = 16;

    /// <summary>
    /// Whether litd would refuse <paramref name="label"/> for looking like an account id.
    /// </summary>
    /// <remarks>
    /// litd's <c>checkLabel</c> rejects a label that is exactly an id's length and parses as hex,
    /// because every call that takes "id or label" could no longer tell which it was given. Its error
    /// comes back as a generic RPC failure, so the form applies the same rule first and says so plainly.
    /// </remarks>
    public static bool LooksLikeAnAccountId(string? label) =>
        label is { Length: AccountIdHexLength } && label.All(Uri.IsHexDigit);

    /// <summary>
    /// The moment an account picked to expire on <paramref name="date"/> should stop working.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The last tick of that day in UTC, not its first: someone choosing "expires on the 30th" means the
    /// account is good for the 30th, and midnight would cut it a day short. litd truncates to whole
    /// seconds on the way in, which is why this does not need to be exact beyond the second.
    /// </para>
    /// <para>
    /// The kind is stripped rather than trusted. A date picker gives a calendar date, not an instant,
    /// but the model binder decides what <see cref="DateTimeKind"/> to hang on it - and
    /// <c>new DateTimeOffset(local, TimeSpan.Zero)</c> throws outright on a server whose clock is not
    /// UTC, which would be a 500 on the form rather than a validation error.
    /// </para>
    /// </remarks>
    public static DateTimeOffset EndOfDayUtc(DateTime date) =>
        new(DateTime.SpecifyKind(date.Date.AddDays(1).AddTicks(-1), DateTimeKind.Unspecified), TimeSpan.Zero);
}
