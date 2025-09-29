using BeanBot.Entities;
using BeanBot.Util;
using CsvHelper;
using Discord.WebSocket;
using Serilog;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace BeanBot.EventHandlers
{
    public sealed class PunHandler : IAsyncDisposable
    {
        private readonly DiscordSocketClient _discordClient;
        private readonly ulong _generalChannelId;
        private readonly CancellationTokenSource _tokenSource = new();
        private Task? _runner;

        private static readonly TimeSpan PostTimeLocal = new TimeSpan(16, 20, 0);

        public PunHandler(DiscordSocketClient discordSocketClient)
        {
            Log.Information("Initializing Daily Pun Posting Service");
            _discordClient = discordSocketClient ?? throw new ArgumentNullException(nameof(discordSocketClient));
            _generalChannelId = ulong.Parse(AppSettings.Settings["generalChannelId"]);
        }

        public void Start()
        {
            _runner = Task.Run(() => RunAsync(_tokenSource.Token));
        }

        private async Task RunAsync(CancellationToken token)
        {
            var timezone = GetChicagoTimeZone();

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var nextRunUtc = ComputeNextOccurenceUtc(timezone, PostTimeLocal);
                    var delay = nextRunUtc - DateTimeOffset.UtcNow;
                    if (delay < TimeSpan.Zero)
                    {
                        delay = TimeSpan.Zero;
                    }

                    var chicagoNow = GetChicagoNow(timezone);
                    Log.Information("Next pun scheduled for {NextLocal} Chicago ({NextUtc} UTC). Now: {NowLocal} Chicago",
                        TimeZoneInfo.ConvertTime(nextRunUtc, timezone).ToString("yyyy-MM-dd HH:mm:ss"),
                        nextRunUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        chicagoNow.ToString("yyyy-MM-dd HH:mm:ss"));

                    await Task.Delay(delay, token);

                    await PostDailyAsync(timezone, token);
                }
                catch (OperationCanceledException)
                {
                    Log.Warning("Shutting down pun service");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error in PunHandler loop; retrying in 30s");
                    await Task.Delay(TimeSpan.FromSeconds(30), token);
                }
            }
        }

        private async Task PostDailyAsync(TimeZoneInfo timezone, CancellationToken token)
        {
            var chicagoNow = GetChicagoNow(timezone);
            Log.Information("Posting daily pun at {LocalTime} Chicago", chicagoNow);

            var channel = _discordClient.GetChannel(_generalChannelId) as SocketTextChannel;
            if (channel is null)
            {
                Log.Error("Could not find general channel with ID {ChannelId} to post daily pun", _generalChannelId);
                return;
            }

            await channel.SendMessageAsync("Bean Bot ~~Episode~~ Year 4: A New Vtuber Hope, Bump Gets Banned");
            await channel.SendMessageAsync("<:420stolfoit:675553715759087618>");

            try
            {
                using var reader = new StreamReader("Resources/puns.csv");
                using var punCsv = new CsvReader(reader, CultureInfo.InvariantCulture);
                var puns = punCsv.GetRecords<Pun>()
                    .Where(pun => !string.IsNullOrEmpty(pun.BadPost))
                    .ToList();

                if (puns.Count == 0)
                {
                    Log.Error("No puns found in puns.csv");
                    return;
                }

                var randomIndex = RandomNumberGenerator.GetInt32(0, puns.Count);
                var randomPun = puns[randomIndex];

                await channel.SendMessageAsync(randomPun.BadPost);
            }
            catch (FileNotFoundException)
            {
                Log.Error("puns.csv file not found; cannot post daily pun");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error occurred while posting daily pun");
            }
        }

        private static TimeZoneInfo GetChicagoTimeZone()
        {
            try
            {
                // If on Linux or MacOS
                return TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
            }
            catch (TimeZoneNotFoundException)
            {
                // If on Windows
                return TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
            }
        }

        private static DateTimeOffset GetChicagoNow(TimeZoneInfo timezone)
            => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timezone);


        private static DateTimeOffset ComputeNextOccurenceUtc(TimeZoneInfo timezone, TimeSpan localTime)
        {
            var currentChicagoTime = GetChicagoNow(timezone);
            var tentativeNextPostTime = new DateTime(
                currentChicagoTime.Year,
                currentChicagoTime.Month,
                currentChicagoTime.Day,
                localTime.Hours,
                localTime.Minutes,
                localTime.Seconds,
                DateTimeKind.Unspecified
            );

            if (currentChicagoTime.TimeOfDay >= localTime)
            {
                tentativeNextPostTime = tentativeNextPostTime.AddDays(1);
            }

            if (timezone.IsInvalidTime(tentativeNextPostTime))
            {
                tentativeNextPostTime = tentativeNextPostTime.AddHours(1);
            }
            else if (timezone.IsAmbiguousTime(tentativeNextPostTime))
            {
                return new DateTimeOffset(tentativeNextPostTime, timezone.GetAmbiguousTimeOffsets(tentativeNextPostTime)[0]);
            }

            var utc = TimeZoneInfo.ConvertTimeToUtc(tentativeNextPostTime, timezone);

            return new DateTimeOffset(utc, TimeSpan.Zero);
        }


        public async ValueTask DisposeAsync()
        {
            try
            {
                _tokenSource.Cancel();
                if (_runner is not null)
                {
                    await _runner.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            _tokenSource.Dispose();
        }
    }
}