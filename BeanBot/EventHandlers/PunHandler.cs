using BeanBot.Configuration;
using BeanBot.Services;
using BeanBot.Util;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

namespace BeanBot.EventHandlers;

public sealed class PunHandler(
    GatewayClient client,
    DiscordReadySignal readySignal,
    IOptions<BeanBotOptions> options,
    IPunCatalogService punCatalogService,
    ILogger<PunHandler> logger) : BackgroundService
{
    private static readonly TimeSpan PostTimeLocal = new(16, 20, 0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await readySignal.WaitAsync(stoppingToken);
        var chicagoTimeZone = GetChicagoTimeZone();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var nextRunUtc = ComputeNextOccurrenceUtc(chicagoTimeZone, PostTimeLocal);
                var delay = nextRunUtc - DateTimeOffset.UtcNow;
                if (delay < TimeSpan.Zero)
                {
                    delay = TimeSpan.Zero;
                }

                logger.LogDebug("Next daily pun scheduled for {NextRunUtc}", nextRunUtc);
                await Task.Delay(delay, stoppingToken);
                await PostPunAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Pun background service is stopping");
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Unexpected error in the daily pun loop; retrying in 30 seconds");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }

    private async Task PostPunAsync(CancellationToken cancellationToken)
    {
        var channel = FindConfiguredChannel();
        if (channel is null)
        {
            logger.LogWarning("Could not find the configured general channel {ChannelId} for the daily pun", options.Value.GeneralChannelId);
            return;
        }

        var puns = await punCatalogService.GetPunsAsync(cancellationToken);
        if (puns.Count == 0)
        {
            logger.LogWarning("Skipping scheduled pun because no pun records were loaded");
            return;
        }

        await channel.SendMessageAsync(new MessageProperties
        {
            Content = "The time has come and so have I, Bean Bot here to deliver you your daily pun(?)",
        }, cancellationToken: cancellationToken);

        await channel.SendMessageAsync(new MessageProperties
        {
            Content = "<:420stolfoit:675553715759087618>",
        }, cancellationToken: cancellationToken);

        var selectedPun = puns[Random.Shared.Next(puns.Count)];
        await channel.SendMessageAsync(new MessageProperties
        {
            Content = selectedPun,
        }, cancellationToken: cancellationToken);

        logger.LogDebug("Posted scheduled pun to channel {ChannelId}", channel.Id);
    }

    private TextChannel? FindConfiguredChannel()
    {
        foreach (var guild in client.Cache.Guilds.Values)
        {
            if (guild.Channels.TryGetValue(options.Value.GeneralChannelId, out var channel) &&
                channel is TextChannel textChannel)
            {
                return textChannel;
            }
        }

        return null;
    }

    private static TimeZoneInfo GetChicagoTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
        }
    }

    private static DateTimeOffset ComputeNextOccurrenceUtc(TimeZoneInfo timezone, TimeSpan localTime)
    {
        var currentLocalTime = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timezone);
        var tentativeNextPostTime = new DateTime(
            currentLocalTime.Year,
            currentLocalTime.Month,
            currentLocalTime.Day,
            localTime.Hours,
            localTime.Minutes,
            localTime.Seconds,
            DateTimeKind.Unspecified);

        if (currentLocalTime.TimeOfDay >= localTime)
        {
            tentativeNextPostTime = tentativeNextPostTime.AddDays(1);
        }

        if (timezone.IsInvalidTime(tentativeNextPostTime))
        {
            tentativeNextPostTime = tentativeNextPostTime.AddHours(1);
        }

        if (timezone.IsAmbiguousTime(tentativeNextPostTime))
        {
            return new DateTimeOffset(tentativeNextPostTime, timezone.GetAmbiguousTimeOffsets(tentativeNextPostTime)[0]);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(tentativeNextPostTime, timezone), TimeSpan.Zero);
    }
}
