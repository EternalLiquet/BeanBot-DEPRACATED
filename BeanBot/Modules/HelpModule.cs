using Discord;
using Discord.Commands;
using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BeanBot.Modules
{
    [Name("Command Information/Help")]
    public class HelpModule : ModuleBase<SocketCommandContext>
    {
        private readonly CommandService _commandService;

        public HelpModule(CommandService commandService)
        {
            _commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));
        }

        [Command("help")]
        [Summary("Lists all the commands that Bean Bot is able to use")]
        public async Task HelpCommand()
        {
            var helpBuilder = new EmbedBuilder
            {
                Title = "Bean Bot Commands",
                Description = "These are the commands that are available to you\nTo use them, type % or succ followed by any of the commands below.\nEx: %shine or succ shine",
                Color = new Color(218, 112, 214),
                ThumbnailUrl = "https://cdn.discordapp.com/avatars/630470467261693982/91f45a4463007f73ff73ff5178847056.png?size=256"
            };

            foreach (var module in _commandService.Modules)
            {
                var description = new StringBuilder();
                foreach (var command in module.Commands)
                {
                    var result = await command.CheckPreconditionsAsync(Context);
                    if (!result.IsSuccess)
                    {
                        continue;
                    }

                    description.Append("**").Append(command.Aliases.First()).AppendLine("**");
                    if (command.Aliases.Count > 1)
                    {
                        description.AppendLine(command.Aliases.Count > 2
                            ? "This command can also be used with the following aliases:"
                            : "This command can also be used with the following alias:");
                        foreach (var alias in command.Aliases.Skip(1))
                        {
                            description.Append('\t').Append('*').Append(alias).AppendLine("*");
                        }
                    }

                    description.AppendLine();
                }

                if (description.Length > 0)
                {
                    helpBuilder.AddField($"**=== {module.Name} ===**", description.ToString());
                }
            }

            await ReplyAsync(string.Empty, false, helpBuilder.Build());
        }
    }
}
