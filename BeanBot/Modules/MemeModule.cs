using BeanBot.Entities;
using BeanBot.Util;
using CsvHelper;
using Discord;
using Discord.Commands;
using Discord.Addons.Interactive;
using Discord.WebSocket;
using Newtonsoft.Json;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace BeanBot.Modules
{
    [Name("Meme Commands")]
    public class MemeModule : InteractiveBase
    {
        private static HttpClient httpClient = new HttpClient();

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

        [Command("fancy ocho ocho")]
        [Summary("Everyone that went to the Music Box is banned from this server")]
        [Remarks("succ ocho ocho")]
        [RequireBotPermission(ChannelPermission.SendMessages)]
        public async Task OchoOcho2()
        {
            await Task.Factory.StartNew(() => { _ = ReplyWithOchoOcho2(); });
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

        [Command("boom")]
        [Summary("Recites a crimson demon's signature chant")]
        [Alias("explosion")]
        [RequireBotPermission(ChannelPermission.SendMessages)]
        public async Task Explosion()
        {
            await Task.Factory.StartNew(() => { _ = ReplyWithEXPLOSION(); });
        }

        [Command("8ball")]
        [Summary("Let me predict your future.. for a price")]
        [Alias("fortune")]
        [RequireBotPermission(ChannelPermission.SendMessages)]
        public async Task EightBall([Remainder] string question)
        {
            await Task.Factory.StartNew(() => { _ = ChooseRandomAnswer(question); });
        }

        [Command("pun")]
        [Summary("I will give you one PunMaster™ branded pun")]
        [RequireBotPermission(ChannelPermission.SendMessages)]
        public async Task Pun()
        {
            await Task.Factory.StartNew(() =>
            {
                _ = ChooseRandomPun();
            });
        }

        [Command("meme")]
        [Summary("Will give you a random meme from reddit")]
        [Remarks("meme")]
        [RequireBotPermission(ChannelPermission.SendMessages)]
        public async Task Meme(string subreddit = "")
        {
            await Task.Factory.StartNew(() => { _ = InvokeMemeApi(subreddit); });
        }
        private async Task InvokeMemeApi(string subreddit)
        {
            string uri;
            if (string.IsNullOrEmpty(subreddit)) uri = $"https://meme-api.herokuapp.com/gimme";
            else uri = $"https://meme-api.herokuapp.com/gimme/{HttpUtility.UrlEncode(subreddit)}";
            HttpResponseMessage response = await httpClient.GetAsync(requestUri: uri);
            if (!response.IsSuccessStatusCode) await ReplyAsync("The meme machine is down, quick, call 911!");
            else
            {
                MemeResponse meme = JsonConvert.DeserializeObject<MemeResponse>(await response.Content.ReadAsStringAsync());
                EmbedBuilder memeBuilder = new EmbedBuilder()
                {
                    Title = meme.title,
                    Description = $"/r/{meme.subreddit}",
                    ImageUrl = meme.url
                };
                await ReplyAsync(embed: memeBuilder.Build());
            }
        }


        private async Task ChooseRandomPun()
        {
            using (var reader = new StreamReader("Resources/puns.csv"))
            using (var csv = new CsvReader(reader, CultureInfo.InvariantCulture))
            {
                var records = csv.GetRecords<Pun>();
                List<string> punArray = new List<string>();
                foreach (var record in records)
                {
                    punArray.Add(record.BadPost);
                }
                var random = new Random();
                var index = random.Next(punArray.Count());
                await ReplyAsync(punArray.ElementAt(index));
            }
        }

        private async Task ChooseRandomAnswer(string question)
        {
            if (IsQuestion(question))
            {
                Random random = new Random();
                var answer = eightBallResponses[random.Next(0, eightBallResponses.Length)];
                await ReplyAsync($"> {question} \n{answer}");
            }
            else
            {
                Random random = new Random();
                var gordonGif = random.Next(1, 9);
                await Context.Channel.SendFileAsync($"Resources/gordon{gordonGif}.gif", $"> {question} \nThat is not a question");
            }
        }

        private bool IsQuestion(string question)
        {
            return question.EndsWith('?');
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

        private async Task ReplyWithOchoOcho2()
        {
            var pages = new[] { "One plus one, equals two.", "Two plus two, equals four.", "Four plus four, equals eight.", "Doblehin ang eight.", "Tayo'y mag ocho ocho, ocho ocho, mag ocho ocho pa" };
            await PagedReplyAsync(pages);
        }

        private async Task ReplyWithEXPLOSION()
        {
            await ReplyAsync("Darkness blacker than black and darker than dark,");
            Thread.Sleep(1500);
            await ReplyAsync("I beseech thee, combine with my deep crimson.");
            Thread.Sleep(1500);
            await ReplyAsync("The time of awakening cometh.");
            Thread.Sleep(1500);
            await ReplyAsync("Justice, fallen upon the infallible boundary,");
            Thread.Sleep(1500);
            await ReplyAsync("appear now as an intangible distortions!");
            Thread.Sleep(1500);
            await ReplyAsync("I desire for my torrent of power a destructive force:");
            Thread.Sleep(1500);
            await ReplyAsync("a destructive force without equal!");
            Thread.Sleep(1500);
            await ReplyAsync("Return all creation to cinders,");
            Thread.Sleep(1500);
            await ReplyAsync("and come from the abyss!");
            Thread.Sleep(1500);
            var msg = await ReplyAsync("E");
            Thread.Sleep(100);
            await msg.ModifyAsync(m => { m.Content = "EX"; });
            Thread.Sleep(100);
            await msg.ModifyAsync(m => { m.Content = "EXP"; });
            Thread.Sleep(100);
            await msg.ModifyAsync(m => { m.Content = "EXPL"; });
            Thread.Sleep(100);
            await msg.ModifyAsync(m => { m.Content = "EXPLO"; });
            Thread.Sleep(100);
            await msg.ModifyAsync(m => { m.Content = "EXPLOS"; });
            Thread.Sleep(100);
            await msg.ModifyAsync(m => { m.Content = "EXPLOSI"; });
            Thread.Sleep(100);
            await msg.ModifyAsync(m => { m.Content = "EXPLOSIO"; });
            Thread.Sleep(100);
            await msg.ModifyAsync(m => { m.Content = "EXPLOSION"; });
            Thread.Sleep(100);
            await msg.ModifyAsync(m => { m.Content = "EXPLOSION!"; });
        }
    }
    public class MemeResponse
    {
        public string postLink { get; set; }
        public string subreddit { get; set; }
        public string title { get; set; }
        public string url { get; set; }
        public bool nsfw { get; set; }
        public bool spoiler { get; set; }
    }
}
