using System.Text.Json;
using StickerApp;
using Xunit;

namespace StickerApp.Tests;

public class ExpiryTests
{
    private static readonly DateTimeOffset Now =
        new(new DateTime(2026, 11, 16, 15, 30, 0, DateTimeKind.Local));

    [Fact]
    public void Relative_choices_add_their_duration()
    {
        Assert.Equal(Now.AddHours(1), Expiry.Resolve(Expiry.Choice.OneHour, Now));
        Assert.Equal(Now.AddDays(1), Expiry.Resolve(Expiry.Choice.OneDay, Now));
        Assert.Equal(Now.AddDays(7), Expiry.Resolve(Expiry.Choice.OneWeek, Now));
    }

    [Fact]
    public void End_of_today_is_the_next_local_midnight()
    {
        var at = Expiry.Resolve(Expiry.Choice.EndOfToday, Now);
        Assert.Equal(new DateTime(2026, 11, 17, 0, 0, 0), at.LocalDateTime);
    }

    [Fact]
    public void IsExpired_only_once_the_deadline_is_reached()
    {
        Assert.False(Expiry.IsExpired(null, Now));
        Assert.False(Expiry.IsExpired(Now.AddSeconds(1), Now));
        Assert.True(Expiry.IsExpired(Now, Now));
        Assert.True(Expiry.IsExpired(Now.AddMinutes(-5), Now));
    }

    [Theory]
    [InlineData(30, "<1m")]                     // seconds
    [InlineData(45 * 60, "45m")]
    [InlineData(3 * 3600, "3h")]
    [InlineData(3 * 3600 + 12 * 60, "3h 12m")]
    [InlineData(2 * 86400, "2d")]
    [InlineData(2 * 86400 + 4 * 3600, "2d 4h")]
    public void FormatRemaining_is_compact(int seconds, string expected) =>
        Assert.Equal(expected, Expiry.FormatRemaining(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void ExpiresAt_round_trips_through_session_json_and_defaults_to_null()
    {
        var at = Now.AddHours(1);
        string json = JsonSerializer.Serialize(new[]
        {
            new StickerState { Source = @"C:\a.png", ExpiresAt = at },
            new StickerState { Source = @"C:\b.png" },
        });
        Assert.Contains("\"expires_at\"", json);

        var states = Session.Parse(json);
        Assert.Equal(at, states[0].ExpiresAt);
        Assert.Null(states[1].ExpiresAt);
    }
}
