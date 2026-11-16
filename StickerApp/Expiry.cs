namespace StickerApp;

/// <summary>
/// Auto-expiry choices for "Remove later" and the helpers that turn them into
/// deadlines and countdown text. Pure (the clock is passed in) so it's unit-testable.
/// </summary>
internal static class Expiry
{
    public enum Choice { OneHour, EndOfToday, OneDay, OneWeek }

    public static readonly IReadOnlyList<(Choice Choice, string Label)> Choices = new[]
    {
        (Choice.OneHour, "In 1 hour"),
        (Choice.EndOfToday, "At end of today"),
        (Choice.OneDay, "In 1 day"),
        (Choice.OneWeek, "In 1 week"),
    };

    /// <summary>The deadline for <paramref name="choice"/>, measured from <paramref name="now"/>.
    /// "End of today" is the next local midnight.</summary>
    public static DateTimeOffset Resolve(Choice choice, DateTimeOffset now) => choice switch
    {
        Choice.OneHour => now.AddHours(1),
        Choice.EndOfToday => new DateTimeOffset(now.LocalDateTime.Date.AddDays(1)),
        Choice.OneDay => now.AddDays(1),
        Choice.OneWeek => now.AddDays(7),
        _ => throw new ArgumentOutOfRangeException(nameof(choice)),
    };

    public static bool IsExpired(DateTimeOffset? expiresAt, DateTimeOffset now) =>
        expiresAt is { } at && at <= now;

    /// <summary>Compact countdown: "&lt;1m", "45m", "3h 12m", "2d 4h".</summary>
    public static string FormatRemaining(TimeSpan left)
    {
        if (left < TimeSpan.FromMinutes(1))
            return "<1m";
        if (left < TimeSpan.FromHours(1))
            return $"{(int)left.TotalMinutes}m";
        if (left < TimeSpan.FromDays(1))
            return left.Minutes == 0 ? $"{(int)left.TotalHours}h" : $"{(int)left.TotalHours}h {left.Minutes}m";
        return left.Hours == 0 ? $"{left.Days}d" : $"{left.Days}d {left.Hours}h";
    }
}
