using BeanBot.Util;
using Discord;
using Discord.WebSocket;
using Serilog;
using System.IO;
using System.Threading.Tasks;

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
                LogLevel = LogSeverity.Verbose, 
                MessageCacheSize = 50
            });
            _token = Support.BotToken;
            try
            {
                await _client.LoginAsync(TokenType.Bot, _token);
                await _client.StartAsync();
            }
            catch (Discord.Net.HttpException e)
            {
                Log.Error(e.ToString());
                Log.Error($"Bean Token was incorrect, please review the bean token file in {Path.GetFullPath(TokenSetup.botTokenFilePath)}");
            }

            _client.Log += LogMessages;
            await Task.Delay(-1);
        }

        private Task LogMessages(LogMessage messages)
        {
            string formattedMessage = $"Discord:\t{messages.Source.ToString()}\t{messages.Message.ToString()}";
            switch (messages.Severity)
            {
                case LogSeverity.Critical:
                    Log.Fatal(formattedMessage);
                    break;
                case LogSeverity.Error:
                    Log.Error(formattedMessage);
                    break;
                case LogSeverity.Warning:
                    Log.Warning(formattedMessage);
                    break;
                case LogSeverity.Info:
                    Log.Information(formattedMessage);
                    break;
                case LogSeverity.Verbose:
                    Log.Verbose(formattedMessage);
                    break;
                default:
                    Log.Information($"Log Severity: {messages.Severity}");
                    Log.Information(formattedMessage);
                    break;
            }
            return Task.CompletedTask;
        }
    }
}
