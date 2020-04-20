using BeanBot.Util;
using Discord.WebSocket;
using Serilog;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Timers;

namespace BeanBot.EventHandlers
{
    public class TimeBasedAutoPostHandler
    {
        public System.Timers.Timer timer;
        private DiscordSocketClient _discordClient;
        Thread timerThread;

        public TimeBasedAutoPostHandler(DiscordSocketClient discordClient)
        {
            Log.Information("Instantiating Time Based Auto-Posting");
            _discordClient = discordClient;
            timerThread = new Thread(new ThreadStart(SendEventEveryMinute));
        }

        public void StartTimer() 
        {
            timerThread.Start();
        }
        private void SendEventEveryMinute()
        {
            Log.Information("Initializing the timer class and starting the thread on which it will run");
            timer = new System.Timers.Timer(60000);
            timer.Start();
            timer.Elapsed += PostRubyHigh;
        }

        private void PostRubyHigh(Object sender, ElapsedEventArgs args)
        {
            Log.Verbose($"Sender: {sender.ToString()}");
            Log.Verbose($"Arg: {args.ToString()}");
            Log.Verbose($"It is currently {DateTime.Now.TimeOfDay}");
            Log.Verbose($"The minute is: {DateTime.Now.Minute}");
            if (DateTime.Now.Minute == 20)
            {
                var discordChannel = _discordClient.GetChannel(436625112112955407) as SocketTextChannel;
                discordChannel.SendMessageAsync("🎉It's OFFICIALLY 4:20🎉");
                discordChannel.SendMessageAsync("<:420stolfoit:675553715759087618>");
                discordChannel.SendMessageAsync("https://www.youtube.com/watch?v=QZXc39hT8t4");
                var discordChannel2 = _discordClient.GetChannel(630501319295107083) as SocketTextChannel;
                discordChannel2.SendMessageAsync("It's 4:20 SOMEWHERE in the world");
                discordChannel2.SendMessageAsync("<:420stolfoit:675553715759087618>");
            }
        }
    }
}
