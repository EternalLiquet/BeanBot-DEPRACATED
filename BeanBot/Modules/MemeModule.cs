using Discord;
using Discord.Commands;
using Discord.WebSocket;

using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using Serilog;

namespace BeanBot.Modules
{
    [Name("Meme Commands")]
    public class MemeModule : ModuleBase<SocketCommandContext>
    {
        [Command("succ")]
        [Summary("Astolfo will suck your dick and call you gay")]
        [Alias("succ succ", "cursed bean")]
        [Remarks("succ succ")]
        [RequireBotPermission(ChannelPermission.SendMessages)]
        public async Task UserSucc([Summary("The (optional) user to succ")] SocketUser user = null)
        {
            var userInformation = user ?? Context.Message.Author;
            await ReplyAsync($"{userInformation.Mention} *succ succ succ* lol you're gay");
        }

        [Command("2am")]
        [Summary("There's only one thing to do at 2 AM...")]
        [Alias("mcdonalds")]
        [Remarks("succ mcdonalds")]
        [RequireBotPermission(ChannelPermission.SendMessages)]
        public async Task McDonalds()
        {
            await ReplyAsync("<:mcdonalds:661337575704887337>");
        }

        [Command("ocho ocho")]
        [Summary("Everyone that went to the Music Box is banned from this server")]
        [Alias("one plus one", "two plus two", "four plus four", "doblehin ang eight")]
        [Remarks("succ ocho ocho")]
        [RequireBotPermission(ChannelPermission.SendMessages)]
        public async Task OchoOcho()
        {
            //Log.Verbose($"Threads Running Before Task Factory: { Process.GetCurrentProcess().Threads.Count }");
            //await Task.Factory.StartNew(() => ReplyWithOchoOcho());
            //Log.Verbose($"Threads Running After Task Factory: { Process.GetCurrentProcess().Threads.Count }");
            Log.Verbose($"Threads Running Before Thread: { Process.GetCurrentProcess().Threads.Count }");
            Thread ochoOchoThread = new Thread(() => ReplyWithOchoOcho());
            ochoOchoThread.Start();
            Log.Verbose($"Threads Running After Thread: { Process.GetCurrentProcess().Threads.Count }");
        }

        private async Task ReplyWithOchoOcho()
        {
            Log.Verbose($"Threads Running During Task Factory: { Process.GetCurrentProcess().Threads.Count }");
            await ReplyAsync("One plus one, equals two.");
            Thread.Sleep(1000);
            await ReplyAsync("Two plus two, equals four.");
            Thread.Sleep(1000);
            await ReplyAsync("Four plus four, equals eight.");
            Thread.Sleep(1000);
            await ReplyAsync("Doblehin ang eight.");
            Thread.Sleep(1000);
            await ReplyAsync("Tayo'y mag ocho ocho, ocho ocho, mag ocho ocho pa");
        }
    }
}
