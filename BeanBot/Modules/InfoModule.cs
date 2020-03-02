using Discord;
using Discord.Commands;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;

namespace BeanBot.Modules
{
    [Name("Bot Information")]
    public class InfoModule : ModuleBase<SocketCommandContext>
    {
        [Command("dev")]
        [Summary("Tags the lead developer on Discord")]
        [Remarks("succ dev")]
        [RequireBotPermission(ChannelPermission.SendMessages)]
        public async Task DeveloperCommand()
        {
            long leadDeveloperDiscordUserId = 114559039731531781;
            await ReplyAsync($"<@{leadDeveloperDiscordUserId}> is my lead developer");
        }

        [Command("patch")]
        public async Task PatchCommand()
        {
            HttpClient client = new HttpClient();
            var result = await client.PostAsync("https://localhost:5001/payload", null);
            var stuff = await result.Content.ReadAsStringAsync();
            await ReplyAsync(stuff);
        }
    }
}
