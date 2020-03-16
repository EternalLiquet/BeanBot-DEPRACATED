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
        private string[] eightBallResponses = new string[8]
        {
            "Hell yeah brother",
            "Yeehaw",
            "The answer is yes if you let me suck your toes",
            "It is unclear, let me succ you and try asking again",
            "The spirit of Texas tells me No",
            "Yes uwu",
            "No umu",
            "*succ succ succ* lol you're gay"
        };

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
            await Task.Factory.StartNew(() => { _ = ReplyAsync("<420stofloit:681383684175167508>"); });
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

        [Command("echo")]
        [Summary("Gives the bot braincells")]
        [Alias("say")]
        [RequireBotPermission(ChannelPermission.SendMessages)]
        public async Task Echo([Remainder] string text)
        {
            await Task.Factory.StartNew(() => 
            {
                _ = Context.Message.DeleteAsync();
                _ = ReplyAsync(text); 
            });
        }

        [Command("boom test")]
        [Summary("Recites a crimson demon's signature chant")]
        [Alias("explosion")]
        [RequireBotPermission(ChannelPermission.SendMessages)]
        public async Task Explosion()
        {
            await Task.Factory.StartNew(() => { _ = ReplyWithExplosion(); });
        }

        [Command("8ball")]
        [Summary("Let me predict your future.. for a price")]
        [Alias("fortune")]
        [RequireBotPermission(ChannelPermission.SendMessages)]
        public async Task EightBall([Remainder] string allowInput)
        {
            await Task.Factory.StartNew(() => { _ = ChooseRandomAnswer(); });
        }

        private async Task ChooseRandomAnswer()
        {
            Random random = new Random();
            var answer = eightBallResponses[random.Next(0, eightBallResponses.Length)];
            await ReplyAsync(answer);
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

        private async Task ReplyWithExplosion()
        {
            await ReplyAsync("Darkness blacker than black and darker than dark, I beseech thee, combine with my deep crimson.\nThe time of awakening cometh.");
            Thread.Sleep(1500);
            await ReplyAsync("Justice, fallen upon the infallible boundary, appear now as an intangible distortions!");
            Thread.Sleep(1500);
            await ReplyAsync("I desire for my torrent of power a destructive force: a destructive force without equal!");
            Thread.Sleep(1500);
            await ReplyAsync("Return all creation to cinders, and come from the abyss!");
            Thread.Sleep(1500);
            await ReplyAsync("EXPLOSION");
        }
    }
}
