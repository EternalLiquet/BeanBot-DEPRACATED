using System;
using System.Threading.Tasks;

using Discord;
using Discord.WebSocket;

using Serilog;
using Serilog.Sinks;
using Serilog.Configuration;

using BeanBot.Util;
using System.IO;

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
            _client = new DiscordSocketClient(new DiscordSocketConfig
            {
                LogLevel = LogSeverity.Verbose
            });
            _token = Support.BotToken;
            try
            {
                await _client.LoginAsync(TokenType.Bot, _token);
                await _client.StartAsync();
                //await _client.SetGameAsync();
            }
            catch (Discord.Net.HttpException e)
            {
                Log.Error($"Bean Token was incorrect, please review the bean token file in {Path.GetFullPath(TokenSetup.botTokenFilePath)}");
            }

            _client.Log += (logMessages)
                => LogMessages(logMessages);

            await Task.Delay(-1);
        }

        private async Task LogMessages(LogMessage messages)
        {
            Log.Information($"{messages.Source.ToString()}\t{messages.Message.ToString()}");
        }
    }
}
