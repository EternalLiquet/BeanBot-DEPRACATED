using NetCord.Services.Commands;
using System.ComponentModel;

namespace BeanBot.Modules;

[DisplayName("General")]
[Description("Meta commands and entry points for learning the bot.")]
public sealed class InfoModule : CommandModule<CommandContext>
{
    [Command("dev")]
    [Description("Tags the bot's lead developer.")]
    public Task DeveloperCommandAsync()
        => ReplyAsync(new NetCord.Rest.ReplyMessageProperties
        {
            Content = "<@114559039731531781> is my lead developer",
        });
}
