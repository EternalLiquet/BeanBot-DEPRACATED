using System.Diagnostics.CodeAnalysis;
using BeanBot.Configuration;
using BeanBot.Discord.Commands;
using BeanBot.Discord.Events;
using Discord.WebSocket;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeanBot.Tests.Discord.Events;

public class DailyPunScheduleTests
{
    [Fact]
    public void ComputeNextOccurrenceUtc_UsesConfiguredLocalTimeAndTimeZone()
    {
        var schedule = CreateSchedule("09:00", "Asia/Tokyo");
        var nowUtc = new DateTimeOffset(2026, 1, 15, 1, 0, 0, TimeSpan.Zero);

        var nextUtc = PunHandler.ComputeNextOccurrenceUtc(schedule, nowUtc);

        Assert.Equal(new DateTimeOffset(2026, 1, 16, 0, 0, 0, TimeSpan.Zero), nextUtc);
    }

    [Fact]
    public void ComputeNextOccurrenceUtc_SpringForwardMovesInvalidWallTimeForwardOnce()
    {
        var schedule = CreateSchedule("02:30", "America/Chicago");
        var nowUtc = new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero);

        var nextUtc = PunHandler.ComputeNextOccurrenceUtc(schedule, nowUtc);

        Assert.Equal(new DateTimeOffset(2026, 3, 8, 8, 30, 0, TimeSpan.Zero), nextUtc);
    }

    [Fact]
    public void ComputeNextOccurrenceUtc_FallBackChoosesOneStandardTimeOccurrence()
    {
        var schedule = CreateSchedule("01:30", "America/Chicago");
        var nowUtc = new DateTimeOffset(2026, 11, 1, 5, 0, 0, TimeSpan.Zero);

        var nextUtc = PunHandler.ComputeNextOccurrenceUtc(schedule, nowUtc);

        Assert.Equal(new DateTimeOffset(2026, 11, 1, 7, 30, 0, TimeSpan.Zero), nextUtc);
    }

    [Fact]
    public async Task DisposeAsync_CancelsScheduledDelayWithInjectedClock()
    {
        using var client = new DiscordSocketClient();
        var schedule = CreateSchedule("16:20", "America/Chicago");
        var options = new BeanBotOptions(
            "token",
            "mongodb://localhost",
            1,
            new Uri("https://example.com/hatoete"),
            new Uri("https://example.com/yoshimaru"),
            schedule,
            HealthCheckOptions.Disabled);
        var handler = new PunHandler(
            client,
            options,
            new UnavailablePunProvider(),
            NullLogger<PunHandler>.Instance,
            new FrozenTimeProvider(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero)));

        handler.Start();

        await handler.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
    }

    private static DailyPunSchedule CreateSchedule(string localTime, string timeZoneId)
    {
        Assert.True(DailyPunSchedule.TryParseLocalTime(localTime, out var parsedTime));
        Assert.True(DailyPunSchedule.TryResolveTimeZone(timeZoneId, out var timeZone));
        return new DailyPunSchedule(parsedTime, timeZoneId, timeZone!);
    }

    private sealed class FrozenTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class UnavailablePunProvider : IPunProvider
    {
        public bool TryGetRandomPun([NotNullWhen(true)] out string? pun)
        {
            pun = null;
            return false;
        }
    }
}
