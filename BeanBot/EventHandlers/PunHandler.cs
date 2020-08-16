using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Timers;
using BeanBot.Entities;
using BeanBot.Util;
using CsvHelper;
using Discord.WebSocket;
using Serilog;

namespace BeanBot.EventHandlers
{
    public class PunHandler
    {
        public System.Timers.Timer timer;
        private DiscordSocketClient _discordClient;
        Thread timerThread;

        private readonly ulong generalChannelId = ulong.Parse(AppSettings.Settings["generalChannelId"]);
        public PunHandler(DiscordSocketClient discordClient)
        {
            Log.Information("Instantiating Daily Pun Poster");
            _discordClient = discordClient;
            timerThread = new Thread(new ThreadStart(SendEventEveryHour));
        }
        public void StartTimer()
        {
            timerThread.Start();
        }
        private void SendEventEveryHour()
        {
            Log.Information("Initializing the timer class and starting the thread on which it will run");
            timer = new System.Timers.Timer(60000);
            timer.Start();
            timer.Elapsed += PostRubyHigh;
        }

        private void PostRubyHigh(Object sender, ElapsedEventArgs args)
        {
            var chicagoTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
            var chicagoTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, chicagoTimeZone);
            Log.Verbose($"It is currently {chicagoTime.TimeOfDay} in Chicago");
            Log.Verbose($"The Hour is: {chicagoTime.Hour}");
            using (var reader = new StreamReader("Resources/puns.csv"))
            using (var csv = new CsvReader(reader, CultureInfo.InvariantCulture)) 
            {
                Log.Verbose(csv.ToString());
            }
            if (chicagoTime.Hour == 16 && chicagoTime.Minute == 20)
            {
                using (var reader = new StreamReader("Resources/puns.csv"))
                using (var csv = new CsvReader(reader, CultureInfo.InvariantCulture))
                {
                    var records = csv.GetRecords<Pun>();
                    foreach (var record in records)
                    {
                        if ($"{record.Date.Month}/{record.Date.Day}" == $"{chicagoTime.Date.Month}/{chicagoTime.Date.Day}")
                        {
                            Log.Verbose(record.Date.ToString());
                            Log.Verbose(record.BadPost);
                            var discordChannel = _discordClient.GetChannel(generalChannelId) as SocketTextChannel;
                            discordChannel.SendMessageAsync("Pun Year 2 Electric Boogaloo, The Nightmare Never Ends Edition™");
                            discordChannel.SendMessageAsync("<:420stolfoit:675553715759087618>");
                            discordChannel.SendMessageAsync(record.BadPost);
                        }
                    }
                }
            }
        }
    }
}
