using System;
using System.Threading.Tasks;

using Discord;
using Discord.WebSocket;

using Serilog;
using Serilog.Configuration;

using BeanBot.Util;

namespace BeanBot
{
    class Program
    {
        private DiscordSocketClient _client;
        private string _token;

        static void Main(string[] args)
            => new Program().StartAsync().GetAwaiter().GetResult();

        public async Task StartAsync()
        {
            Support.StartupOperations();
            _token = Support.BotToken;
        }
    }
}
