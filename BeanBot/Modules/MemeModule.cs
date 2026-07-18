using BeanBot.Entities;
using BeanBot.Util;
using CsvHelper;
using Discord;
using Discord.Addons.Interactive;
using Discord.Commands;
using MemeApiDotNetWrapper;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace BeanBot.Modules
{
    [Name("Meme Commands")]
    public class MemeModule : InteractiveBase
    {
        private static readonly HttpClient HttpClient = new HttpClient();
        private readonly MemeMachine memeMachine = new MemeMachine();

        private static readonly string[] EightBallResponses = new string[8]
        {
            "Hell yeah brother",
            "Yeehaw",
            "Yes uwu",
            "The spirit of Texas tells me No",
            "No umu",
            "The answer is yes if you let me suck your toes",
            "It is unclear, let me succ you and try asking again",
            "*succ succ succ* lol you're gay"
        };

        private static readonly string[] TexasFactResponses = new string[8]
        {
            "The tale of the Alamo is retold through the stars",
            "The King Ranch in Texas is bigger than the entire state of California",
            "Texas is the largest country in the world",
            "Texas is the largest exporter of Freedom per capita in the world",
            "Texas boasts the largest herd of wild padorus",
            "Astolfo, the most famous Texan cowboy, was born in Europe",
            "More species of cursed bean live in Texas than any other part of the world",
            "The entire country of Texas has 5 Jollibees"
        };

        [Command("succ")]
        [Summary("Astolfo will suck your dick and call you gay")]
        [Alias("cursed bean")]
        [Remarks("succ")]
        [RequireBotPermission(ChannelPermission.SendMessages)]
        public async Task UserSucc([Summary("The (optional) user to succ")] params string[] input)
        {
            var userToSucc = NormalizeSuccTarget(input, Context.Message.Author.Mention);
            await ReplyAsync($"*succ succ succ* lol you're gay {userToSucc}");
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

        [Command("fancy ocho ocho")]
        [Summary("Everyone that went to the Music Box is banned from this server")]
        [Remarks("succ ocho ocho")]
        [RequireBotPermission(ChannelPermission.SendMessages)]
        public async Task OchoOcho2()
        {
            await ReplyWithOchoOcho();
        }


        [Command("420")]
        [Summary("Astolfour-twenty blaze it")]
        [Alias("blaze", "blaze it", "weed")]
        [RequireBotPermission(ChannelPermission.SendMessages)]
        public async Task BlazeIt()
        {
            await ReplyAsync("<:420stolfoit:675553715759087618>");
        }

        [Command("toes")]
        [Summary("You've doomed yourself, Hatate")]
        [Alias("toe", "kaz", "the toe fetish is just a joke")]
        [RequireBotPermission(ChannelPermission.SendMessages)]
        [RequireBotPermission(ChannelPermission.AttachFiles)]
        public async Task Toes()
        {
            await SendImageFromUrl(AppSettings.Settings["hatoeteUrl"]);
        }

        [Command("yoshimaru")]
        [Summary("The superior ship")]
        [Alias("yohamaru", "canon ship")]
        [RequireBotPermission(ChannelPermission.SendMessages)]
        [RequireBotPermission(ChannelPermission.AttachFiles)]
        public async Task YoshiMaru()
        {
            await SendImageFromUrl(AppSettings.Settings["yoshimaruUrl"]);
        }

        [Command("echo")]
        [Summary("Gives the bot braincells")]
        [Alias("say")]
        [RequireBotPermission(ChannelPermission.SendMessages)]
        public async Task Echo([Remainder] string text)
        {
            try
            {
                await Context.Message.DeleteAsync();
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "Could not delete the source message for echo command {MessageId}", Context.Message.Id);
            }
            await ReplyAsync(text);
        }

        [Command("8ball")]
        [Summary("Let me predict your future.. for a price")]
        [Alias("fortune")]
        [RequireBotPermission(ChannelPermission.SendMessages)]
        public async Task EightBall([Remainder] string question)
        {
            await ChooseRandomAnswer(question);
        }

        [Command("pun")]
        [Summary("I will give you one PunMaster™ branded pun")]
        [RequireBotPermission(ChannelPermission.SendMessages)]
        public async Task Pun()
        {
            await ChooseRandomPun();
        }

        [Command("meme")]
        [Summary("Will give you a random meme from reddit")]
        [Remarks("meme")]
        [RequireBotPermission(ChannelPermission.SendMessages)]
        public async Task Meme(string subreddit = "")
        {
            await InvokeMemeApi(subreddit);
        }
        private async Task InvokeMemeApi(string subreddit)
        {
            Meme meme;
            try
            {
                if (string.IsNullOrEmpty(subreddit))
                {
                    meme = await memeMachine.GetMemeAsync();
                }
                else
                {
                    meme = await memeMachine.GetMemeAsync(subreddit);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "The meme API request failed");
                meme = null;
            }

            if (meme == null)
            {
                await ReplyAsync("The meme machine is down, quick, call 911!");
            }
            else
            {
                EmbedBuilder memeBuilder = new EmbedBuilder()
                {
                    Title = meme.Title,
                    Description = $"/r/{meme.SubReddit}",
                    ImageUrl = meme.ImageUrl
                };
                await ReplyAsync(embed: memeBuilder.Build());
            }
        }

        [Command("texasnationalbird")]
        [Summary("I will educate you on Texas' official national bird")]
        [Remarks("texasnationalbird")]
        [RequireBotPermission(ChannelPermission.SendMessages)]
        public async Task NationalBird()
        {
            await ReplyAsync("The Texas Offical National Bird is the AR-15");
        }

        [Command("texasnationalflower")]
        [Summary("I will educate you on the Texas' official national flower")]
        [Remarks("texasnationalflower")]
        public async Task NationalFlower()
        {
            await ReplyAsync("The Texas Official National Flower is the Jimmy Dean breakfast taco");
        }

        [Command("texasfacts")]
        [Summary("I will give you a random Texas fact")]
        [Remarks("texasfacts")]
        public async Task TexasFacts()
        {
            var fact = TexasFactResponses[Random.Shared.Next(TexasFactResponses.Length)];
            await ReplyAsync($"Did you know: {fact}");
        }


        private async Task ChooseRandomPun()
        {
            using (var reader = new StreamReader("Resources/puns.csv"))
            using (var csv = new CsvReader(reader, CultureInfo.InvariantCulture))
            {
                var puns = csv.GetRecords<Pun>()
                    .Select(record => record.BadPost)
                    .Where(pun => !string.IsNullOrWhiteSpace(pun))
                    .ToList();

                if (puns.Count == 0)
                {
                    Log.Warning("No usable puns were found in Resources/puns.csv");
                    await ReplyAsync("The PunMaster is temporarily out of material.");
                    return;
                }

                await ReplyAsync(puns[Random.Shared.Next(puns.Count)]);
            }
        }

        private async Task ChooseRandomAnswer(string question)
        {
            var responseOverride = FortuneResponseOverrides.GetResponse(question);
            if (responseOverride != null)
            {
                var hasQueuedAnswer = Program.FortuneAnswers.TryReserve(Context.Message.Author.Id, out var reservation);
                await ReplyAsync($"> {question} \n{responseOverride}");
                if (hasQueuedAnswer)
                {
                    Program.FortuneAnswers.Consume(reservation);
                }
            }
            else if (QuestionValidator.IsQuestion(question))
            {
                if (IsPunMaster())
                {
                    await HandlePunMaster(question);
                }
                else
                {
                    if (Program.FortuneAnswers.TryReserve(Context.Message.Author.Id, out var reservation))
                    {
                        if (reservation.Answer == "positive")
                        {
                            var answer = EightBallResponses[Random.Shared.Next(0, 3)];
                            await ReplyAsync($"> {question} \n{answer}");
                        }
                        else
                        {
                            var answer = EightBallResponses[Random.Shared.Next(3, 5)];
                            await ReplyAsync($"> {question} \n{answer}");
                        }
                        Program.FortuneAnswers.Consume(reservation);
                    }
                    else
                    {
                        var answer = EightBallResponses[Random.Shared.Next(EightBallResponses.Length)];
                        await ReplyAsync($"> {question} \n{answer}");
                    }
                }
            }
            else
            {
                var gordonGif = Random.Shared.Next(1, 9);
                var rejection = $"> {question} \nThat is not a question";
                try
                {
                    await Context.Channel.SendFileAsync($"Resources/gordon{gordonGif}.gif", rejection);
                }
                catch (Exception exception)
                {
                    Log.Warning(exception, "Could not attach the invalid-question Gordon GIF");
                    await ReplyAsync(rejection);
                }
            }
        }

        private async Task HandlePunMaster(string question)
        {
            if (question.Contains("post", StringComparison.OrdinalIgnoreCase) && question.Contains("succ", StringComparison.OrdinalIgnoreCase) ||
                question.Contains("rigged", StringComparison.OrdinalIgnoreCase) && !question.Contains("not", StringComparison.OrdinalIgnoreCase) ||
                question.Contains("ban", StringComparison.OrdinalIgnoreCase) && question.Contains("padoru", StringComparison.OrdinalIgnoreCase) && !question.Contains("not", StringComparison.OrdinalIgnoreCase))
            {
                await ReplyAsync($"> {question} \nThe spirit of Texas tells me No");
            }
            else
            {
                var chance = Random.Shared.Next(1, 101);
                if (chance >= 1 && chance <= 10)
                {
                    var positiveAns = EightBallResponses[Random.Shared.Next(0, 3)];
                    await ReplyAsync($"> {question} \n{positiveAns}");
                }
                else if (chance > 10 && chance <= 40)
                {
                    var negativeAns = EightBallResponses[Random.Shared.Next(3, 5)];
                    await ReplyAsync($"> {question} \n{negativeAns}");
                }
                else
                {
                    var succAns = EightBallResponses[Random.Shared.Next(5, 8)];
                    await ReplyAsync($"> {question} \n{succAns}");
                }
            }
        }

        private bool IsPunMaster()
        {
            return (Context.Message.Author.Id == 262010462323998720);
        }

        private async Task SendImageFromUrl(string url)
        {
            try
            {
                using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                using Stream image = await response.Content.ReadAsStreamAsync();
                await Context.Channel.SendFileAsync(image, "image.png");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to download an image from {ImageUrl}", url);
                await ReplyAsync("I couldn't download that image right now.");
            }
        }

        internal static string NormalizeSuccTarget(IEnumerable<string> input, string authorMention)
        {
            var words = (input ?? Enumerable.Empty<string>()).ToList();
            if (words.Count > 0 && string.Equals(words[0], "succ", StringComparison.OrdinalIgnoreCase))
            {
                words.RemoveAt(0);
            }

            var target = string.Join(" ", words).Trim();
            if (string.IsNullOrWhiteSpace(target) ||
                target.Contains("Bean Bot", StringComparison.OrdinalIgnoreCase) ||
                target.Contains("<@!630470467261693982>", StringComparison.Ordinal))
            {
                return authorMention;
            }

            return target;
        }

        private async Task ReplyWithOchoOcho()
        {
            var pages = new[] { "One plus one, equals two.", "Two plus two, equals four.", "Four plus four, equals eight.", "Doblehin ang eight.", "Tayo'y mag ocho ocho, ocho ocho, mag ocho ocho pa" };
            await PagedReplyAsync(pages);
        }
    }
}
