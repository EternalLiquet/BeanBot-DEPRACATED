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
    public class AutoPosting
    {
        public System.Timers.Timer timer;
        private DiscordSocketClient _discordClient;

        public AutoPosting(DiscordSocketClient discordClient)
        {
            Log.Verbose("reached here");
            _discordClient = discordClient;
            Thread timerThread = new Thread(checkEveryThirtySeconds);
            timerThread.Start();
        }
        public void checkEveryThirtySeconds()
        {
            Log.Verbose("reached here");
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
