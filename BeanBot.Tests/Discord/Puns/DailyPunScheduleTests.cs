using System.Diagnostics.CodeAnalysis;
using BeanBot.Configuration;
using BeanBot.Discord.Puns;
using BeanBot.Persistence.Repositories;
using Discord.WebSocket;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeanBot.Tests.Discord.Puns;

public class DailyPunScheduleTests
{
    [Fact]
    public void ComputeNextOccurrenceUtc_UsesConfiguredLocalTimeAndTimeZone()
    {
        var schedule = CreateSchedule("09:00", "Asia/Tokyo");
        var nowUtc = new DateTimeOffset(2026, 1, 15, 1, 0, 0, TimeSpan.Zero);

        var nextUtc = DailyPunService.ComputeNextOccurrenceUtc(schedule, nowUtc);

        Assert.Equal(new DateTimeOffset(2026, 1, 16, 0, 0, 0, TimeSpan.Zero), nextUtc);
    }

    [Fact]
    public void ComputeNextOccurrenceUtc_SpringForwardMovesInvalidWallTimeForwardOnce()
    {
        var schedule = CreateSchedule("02:30", "America/Chicago");
        var nowUtc = new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero);

        var nextUtc = DailyPunService.ComputeNextOccurrenceUtc(schedule, nowUtc);

        Assert.Equal(new DateTimeOffset(2026, 3, 8, 8, 30, 0, TimeSpan.Zero), nextUtc);
    }

    [Fact]
    public void ComputeNextOccurrenceUtc_SpringForwardKeepsAdjustedOccurrenceUntilItPasses()
    {
        var schedule = CreateSchedule("02:30", "America/Chicago");
        var nowUtc = new DateTimeOffset(2026, 3, 8, 8, 15, 0, TimeSpan.Zero);

        var nextUtc = DailyPunService.ComputeNextOccurrenceUtc(schedule, nowUtc);

        Assert.Equal(new DateTimeOffset(2026, 3, 8, 8, 30, 0, TimeSpan.Zero), nextUtc);
    }

    [Fact]
    public void ComputeNextOccurrenceUtc_FallBackChoosesOneStandardTimeOccurrence()
    {
        var schedule = CreateSchedule("01:30", "America/Chicago");
        var nowUtc = new DateTimeOffset(2026, 11, 1, 5, 0, 0, TimeSpan.Zero);

        var nextUtc = DailyPunService.ComputeNextOccurrenceUtc(schedule, nowUtc);

        Assert.Equal(new DateTimeOffset(2026, 11, 1, 7, 30, 0, TimeSpan.Zero), nextUtc);
    }

    [Fact]
    public void ComputeNextOccurrenceUtc_FallBackKeepsChosenOccurrenceDuringFirstRepeatedHour()
    {
        var schedule = CreateSchedule("01:30", "America/Chicago");
        var nowUtc = new DateTimeOffset(2026, 11, 1, 6, 45, 0, TimeSpan.Zero);

        var nextUtc = DailyPunService.ComputeNextOccurrenceUtc(schedule, nowUtc);

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
        var handler = new DailyPunService(
            client,
            options,
            new UnavailablePunProvider(),
            new UnusedClaimStore(),
            NullLogger<DailyPunService>.Instance,
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

    private sealed class UnusedClaimStore : IDailyPunClaimStore
    {
        public Task<DailyPunClaimResult> TryClaimAsync(DateOnly localDate, CancellationToken cancellationToken)
            => throw new InvalidOperationException("No claim is expected before the scheduled time.");
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
