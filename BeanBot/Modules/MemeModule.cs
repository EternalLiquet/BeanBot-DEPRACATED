using BeanBot.Util;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Serilog;
using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace BeanBot.Modules
{
    [Name("Meme Commands")]
    public class MemeModule : ModuleBase<SocketCommandContext>
    {
        [Command("succ")]
        [Summary("Astolfo will suck your dick and call you gay")]
        [Alias("succ succ", "cursed bean")]
        [Remarks("succ")]
        [RequireBotPermission(ChannelPermission.SendMessages)]
        public async Task UserSucc([Summary("The (optional) user to succ")] params string[] input)
        {
            string userToSucc = "";
            if (input[0] == "succ")
                input[0] = "";
            foreach (string word in input)
            {
                userToSucc += word + " ";
            }
            if (userToSucc.Trim() == "")
                userToSucc = null;
            await Task.Factory.StartNew(() => { _ = ReplyAsync($"*succ succ succ* lol you're gay {userToSucc ?? Context.Message.Author.Mention}"); });
        }

        [Command("2am")]
        [Summary("There's only one thing to do at 2 AM...")]
        [Alias("mcdonalds")]
        [Remarks("succ mcdonalds")]
        [RequireBotPermission(ChannelPermission.SendMessages)]
        public async Task McDonalds()
        {
            await Task.Factory.StartNew(() => { _ = ReplyAsync("<:mcdonalds:661337575704887337>"); });
        }

        [Command("ocho ocho")]
        [Summary("Everyone that went to the Music Box is banned from this server")]
        [Alias("one plus one", "two plus two", "four plus four", "doblehin ang eight")]
        [Remarks("succ ocho ocho")]
        [RequireBotPermission(ChannelPermission.SendMessages)]
        public async Task OchoOcho()
        {
            await Task.Factory.StartNew(() => { _ = ReplyWithOchoOcho(); });
        }

        [Command("420")]
        [Summary("Astolfour-twenty blaze it")]
        [Alias("blaze", "blaze it", "weed")]
        [RequireBotPermission(ChannelPermission.SendMessages)]
        public async Task BlazeIt()
        {
            await Task.Factory.StartNew(() => { _ = ReplyAsync("<:420stolfoit:675553715759087618>"); });
        }

        [Command("toes")]
        [Summary("You've doomed yourself, Hatate")]
        [Alias("toe", "kaz", "the toe fetish is just a joke")]
        [RequireBotPermission(ChannelPermission.SendMessages)]
        [RequireBotPermission(ChannelPermission.AttachFiles)]
        public async Task Toes()
        {
            await Task.Factory.StartNew(() => { _ = sendImageFromUrl(AppSettings.Settings["hatoeteUrl"]); });
        }

        [Command("yoshimaru")]
        [Summary("The superior ship")]
        [Alias("yohamaru", "canon ship")]
        [RequireBotPermission(ChannelPermission.SendMessages)]
        [RequireBotPermission(ChannelPermission.AttachFiles)]
        public async Task YoshiMaru()
        {
            await Task.Factory.StartNew(() => { _ = sendImageFromUrl(AppSettings.Settings["yoshimaruUrl"]); });
        }

        private async Task sendImageFromUrl(string url)
        {
            try
            {
                var webClient = new HttpClient();
                var response = await webClient.GetAsync(url);
                Stream image = await response.Content.ReadAsStreamAsync();
                await Context.Channel.SendFileAsync(image, "image.png");
            }
            catch (Exception ex)
            {
                Log.Error(ex.Message);
            }
        }

        private async Task ReplyWithOchoOcho()
        {
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
