using Discord;
using Discord.Commands;

namespace BeanBot.Discord.Commands;

[Name("Bot Information")]
public class InfoModule : ModuleBase<SocketCommandContext>
{
    private readonly LegacyCommandReplySender _replySender;

    public InfoModule(LegacyCommandReplySender replySender)
    {
        _replySender = replySender ?? throw new ArgumentNullException(nameof(replySender));
    }

    [Command("dev")]
    [Summary("Tags the lead developer on Discord")]
    [Remarks("succ dev")]
    [RequireBotPermission(ChannelPermission.SendMessages)]
    public async Task DeveloperCommand()
    {
        const long leadDeveloperDiscordUserId = 114559039731531781;
        await _replySender.SendMessageAsync(
            Context,
            $"<@{leadDeveloperDiscordUserId}> is my lead developer");
    }
}
