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
            timerThread = new Thread(InitializeTimer);
        }
        public void InitializeTimer()
        {
            Log.Information("Initializing the timer class and starting the thread on which it will run");
            timerThread.Start();
            timer = new System.Timers.Timer(3000);
            timer.Start();
            timer.Elapsed += PostRubyHigh;
        }

        private void PostRubyHigh(Object sender, ElapsedEventArgs args)
        {
            Log.Verbose($"Sender: {sender.ToString()}");
            Log.Verbose($"Arg: {args.ToString()}");
            Log.Verbose($"It is currently {DateTime.Now.TimeOfDay}");
        }
    }
}
