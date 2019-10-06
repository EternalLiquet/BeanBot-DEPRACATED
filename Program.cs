using System;
using System.Threading.Tasks;

using Discord;
using Discord.WebSocket;

using Serilog;
using Serilog.Configuration;

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
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console()
                .WriteTo.File("logs\\BeanBotLogs.txt", rollingInterval: RollingInterval.Day)
                .CreateLogger();
        }
    }
}
