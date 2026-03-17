using BeanBot.Configuration;
using BeanBot.Services;
using BeanBot.Util;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetCord.Rest;
using NetCord.Services.Commands;
using System.ComponentModel;

namespace BeanBot.Modules;

[DisplayName("Fun")]
[Description("Low-stakes commands, media posts, memes, and the 8ball.")]
public sealed class MemeModule(
    IOptions<BeanBotOptions> options,
    IPunCatalogService punCatalogService,
    IMemeService memeService,
    EightBallQueueService eightBallQueueService,
    IHttpClientFactory httpClientFactory,
    ILogger<MemeModule> logger) : CommandModule<CommandContext>
{
    private const string ExternalMediaHttpClientName = "ExternalMedia";

    private static readonly string[] EightBallResponses =
    [
        "Hell yeah brother",
        "Yeehaw",
        "Yes uwu",
        "The spirit of Texas tells me No",
        "No umu",
        "The answer is yes if you let me suck your toes",
        "It is unclear, let me succ you and try asking again",
        "*succ succ succ* lol you're gay",
    ];

    private static readonly string[] TexasFacts =
    [
        "The tale of the Alamo is retold through the stars",
        "The King Ranch in Texas is bigger than the entire state of California",
        "Texas is the largest country in the world",
        "Texas is the largest exporter of Freedom per capita in the world",
        "Texas boasts the largest herd of wild padorus",
        "Astolfo, the most famous Texan cowboy, was born in Europe",
        "More species of cursed bean live in Texas than any other part of the world",
        "The entire country of Texas has 5 Jollibees",
    ];

    [Command("succ", "cursedbean")]
    [Description("Posts the classic succ response. If you do not provide a target, the bot targets you instead.")]
    public async Task UserSuccAsync([CommandParameter(Name = "target", Remainder = true)] string? input = null)
    {
        var userToSucc = input?.Trim();
        if (string.IsNullOrWhiteSpace(userToSucc) ||
            userToSucc.Contains("Bean Bot", StringComparison.OrdinalIgnoreCase) ||
            userToSucc.Contains($"<@!{Context.Client.Id}>", StringComparison.Ordinal) ||
            userToSucc.Contains($"<@{Context.Client.Id}>", StringComparison.Ordinal))
        {
            userToSucc = Mention(Context.User.Id);
        }

        logger.LogDebug("Succ command invoked by {UserId} for target {Target}", Context.User.Id, userToSucc);
        await ReplyAsync(new ReplyMessageProperties
        {
            Content = $"*succ succ succ* lol you're gay {userToSucc}",
        });
    }

    [Command("2am", "mcdonalds")]
    [Description("Posts the McDonald's emoji.")]
    public Task McDonaldsAsync()
        => ReplyAsync(new ReplyMessageProperties { Content = "<:mcdonalds:661337575704887337>" });

    [Command("ochoocho", "ocho-ocho", "fancy-ocho-ocho")]
    [Description("Posts the ocho-ocho chant.")]
    public Task OchoOchoAsync()
        => ReplyAsync(new ReplyMessageProperties
        {
            Content = string.Join(Environment.NewLine,
            [
                "One plus one, equals two.",
                "Two plus two, equals four.",
                "Four plus four, equals eight.",
                "Doblehin ang eight.",
                "Tayo'y mag ocho ocho, ocho ocho, mag ocho ocho pa",
            ]),
        });

    [Command("420", "blaze", "blazeit", "weed")]
    [Description("Posts the 420 emoji.")]
    public Task BlazeItAsync()
        => ReplyAsync(new ReplyMessageProperties { Content = "<:420stolfoit:675553715759087618>" });

    [Command("toes", "toe", "kaz", "toefetish")]
    [Description("Posts the configured Hatoete image.")]
    public Task ToesAsync()
        => SendImageFromUrlAsync(options.Value.HatoeteUrl);

    [Command("yoshimaru", "yohamaru", "canonship")]
    [Description("Posts the configured Yoshimaru image.")]
    public Task YoshimaruAsync()
        => SendImageFromUrlAsync(options.Value.YoshimaruUrl);

    [Command("echo", "say")]
    [Description("Deletes the invoking message and reposts the supplied text through the bot.")]
    public async Task EchoAsync([CommandParameter(Name = "text", Remainder = true)] string text)
    {
        var channel = Context.Channel;
        if (channel is null)
        {
            return;
        }

        try
        {
            await channel.DeleteMessageAsync(Context.Message.Id);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Failed to delete echo invocation message {MessageId}", Context.Message.Id);
        }

        await SendAsync(new MessageProperties
        {
            Content = text,
        });
    }

    [Command("8ball", "fortune")]
    [Description("Answers a yes-or-no question with a random response. Questions should end with a question mark.")]
    public Task EightBallAsync([CommandParameter(Name = "question", Remainder = true)] string? question = null)
        => ChooseRandomAnswerAsync(question);

    [Command("pun")]
    [Description("Posts a random pun from the configured pun catalog.")]
    public Task PunAsync()
        => ChooseRandomPunAsync();

    [Command("meme")]
    [Description("Fetches a meme, optionally from a specific subreddit.")]
    public Task MemeAsync([CommandParameter(Name = "subreddit")] string? subreddit = null)
        => SendMemeAsync(subreddit);

    [Command("texasnationalbird", "texas-national-bird")]
    [Description("Posts the official Texas national bird fact.")]
    public Task TexasNationalBirdAsync()
        => ReplyAsync(new ReplyMessageProperties { Content = "The Texas Official National Bird is the AR-15" });

    [Command("texasnationalflower", "texas-national-flower")]
    [Description("Posts the official Texas national flower fact.")]
    public Task TexasNationalFlowerAsync()
        => ReplyAsync(new ReplyMessageProperties { Content = "The Texas Official National Flower is the Jimmy Dean breakfast taco" });

    [Command("texasfacts", "texas-facts")]
    [Description("Posts a random Texas fact.")]
    public Task TexasFactsAsync()
    {
        var fact = TexasFacts[Random.Shared.Next(TexasFacts.Length)];
        return ReplyAsync(new ReplyMessageProperties { Content = $"Did you know: {fact}" });
    }

    private async Task SendMemeAsync(string? subreddit)
    {
        var meme = await memeService.GetMemeAsync(subreddit);
        if (meme is null)
        {
            await ReplyAsync(new ReplyMessageProperties
            {
                Content = "The meme machine is down, quick, call 911!",
            });
            return;
        }

        await SendAsync(new MessageProperties
        {
            Embeds =
            [
                new EmbedProperties
                {
                    Title = meme.Title,
                    Description = $"/r/{meme.SubReddit}",
                    Image = new EmbedImageProperties(meme.ImageUrl),
                },
            ],
        });
    }

    private async Task ChooseRandomPunAsync()
    {
        var puns = await punCatalogService.GetPunsAsync();
        if (puns.Count == 0)
        {
            await ReplyAsync(new ReplyMessageProperties
            {
                Content = "I couldn't find any puns to post right now.",
            });
            return;
        }

        await ReplyAsync(new ReplyMessageProperties
        {
            Content = puns[Random.Shared.Next(puns.Count)],
        });
    }

    private async Task ChooseRandomAnswerAsync(string? question)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            await ReplyAsync(new ReplyMessageProperties
            {
                Content = "That is not a question.",
            });
            return;
        }

        if (!IsQuestion(question))
        {
            var gifNumber = Random.Shared.Next(1, 9);
            var gifPath = Path.Combine(DirectorySetup.ResourcesDirectory, $"gordon{gifNumber}.gif");

            await using var gifStream = File.OpenRead(gifPath);
            await SendAsync(new MessageProperties
            {
                Content = $"> {question}{Environment.NewLine}That is not a question.",
                Attachments =
                [
                    new AttachmentProperties($"gordon{gifNumber}.gif", gifStream),
                ],
            });

            return;
        }

        if (Context.User.Id == 262010462323998720)
        {
            await HandlePunMasterAsync(question);
            return;
        }

        var queuedAnswer = eightBallQueueService.TryDequeue(Context.User.Id);
        var response = queuedAnswer switch
        {
            "positive" => EightBallResponses[Random.Shared.Next(0, 3)],
            "negative" => EightBallResponses[Random.Shared.Next(3, 5)],
            _ => EightBallResponses[Random.Shared.Next(EightBallResponses.Length)],
        };

        await ReplyAsync(new ReplyMessageProperties
        {
            Content = $"> {question}{Environment.NewLine}{response}",
        });
    }

    private async Task HandlePunMasterAsync(string question)
    {
        var lowerQuestion = question.ToLowerInvariant();
        if (lowerQuestion.Contains("post") && lowerQuestion.Contains("succ") ||
            lowerQuestion.Contains("rigged") && !lowerQuestion.Contains("not") ||
            lowerQuestion.Contains("ban") && lowerQuestion.Contains("padoru") && !lowerQuestion.Contains("not"))
        {
            await ReplyAsync(new ReplyMessageProperties
            {
                Content = $"> {question}{Environment.NewLine}The spirit of Texas tells me No",
            });
            return;
        }

        var roll = Random.Shared.Next(1, 101);
        var response = roll switch
        {
            <= 10 => EightBallResponses[Random.Shared.Next(0, 3)],
            <= 40 => EightBallResponses[Random.Shared.Next(3, 5)],
            _ => EightBallResponses[Random.Shared.Next(5, 8)],
        };

        await ReplyAsync(new ReplyMessageProperties
        {
            Content = $"> {question}{Environment.NewLine}{response}",
        });
    }

    private async Task SendImageFromUrlAsync(string url)
    {
        try
        {
            using var response = await httpClientFactory.CreateClient(ExternalMediaHttpClientName).GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using var imageStream = await response.Content.ReadAsStreamAsync();
            await SendAsync(new MessageProperties
            {
                Attachments =
                [
                    new AttachmentProperties("image.png", imageStream),
                ],
            });
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to send image from {Url}", url);
            await ReplyAsync(new ReplyMessageProperties
            {
                Content = "I couldn't fetch that image right now.",
            });
        }
    }

    private static bool IsQuestion(string question)
        => question.TrimEnd().EndsWith("?", StringComparison.Ordinal);

    private static string Mention(ulong userId)
        => $"<@{userId}>";
}
