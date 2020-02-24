using Discord;
using Discord.Commands;
using System.Linq;
using System.Threading.Tasks;

namespace BeanBot.Modules
{
    [Name("Command Information/Help")]
    public class HelpModule : ModuleBase<SocketCommandContext>
    {
        private readonly CommandService _commandService;

        public HelpModule(CommandService commandService)
        {
            _commandService = commandService;
        }

        [Command("help")]
        [Summary("Lists all the commands that Bean Bot is able to use")]
        public async Task HelpCommand()
        {
            char charPrefix = '%';
            string stringPrefix = "succ ";

            EmbedBuilder helpBuilder = new EmbedBuilder()
            {
                Title = "Bean Bot Commands",
                Description = $"These are the commands that are available to you\nTo use them, type {charPrefix} or {stringPrefix} followed by any of the commands below.\n Ex: %shine or succ shine",
                Color = new Color(218, 112, 214),
                ThumbnailUrl = "https://cdn.discordapp.com/avatars/630470467261693982/91f45a4463007f73ff73ff5178847056.png?size=256"
            };

            foreach (var module in _commandService.Modules)
            {
                string description = null;
                foreach (var command in module.Commands)
                {
                    var result = await command.CheckPreconditionsAsync(Context);
                    if (result.IsSuccess)
                    {
                        description += $"**{command.Aliases.First()}**\nFunction: {command.Summary}\n";
                        if (command.Aliases.Count > 1)
                        {
                            description += command.Aliases.Count > 2 ? $"This command can also be used by using the following aliases: \n" : $"This command can also be used by using the following alias: \n";
                            foreach (var alias in command.Aliases)
                            {
                                if (alias != command.Aliases.First())
                                    description += $"\t*{alias}*\n";
                            }
                        }
                        description += "\n";
                    }
                }

                if (!string.IsNullOrWhiteSpace(description))
                {
                    helpBuilder.AddField(field =>
                    {
                        field.Name = $"**=== {module.Name} ===**";
                        field.Value = description;
                        field.IsInline = false;
                    });
                }
            }

            await Context.User.SendMessageAsync("", false, helpBuilder.Build());
        }
    }
}
