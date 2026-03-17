using BeanBot.Services;
using NetCord.Rest;
using NetCord.Services.Commands;
using System.ComponentModel;

namespace BeanBot.Modules;

[DisplayName("General")]
[Description("Meta commands and entry points for learning the bot.")]
public sealed class HelpModule(HelpCatalogService helpCatalogService) : CommandModule<CommandContext>
{
    private const int ModulesPerPage = 3;

    [Command("help")]
    [Description("Shows the module index by default. You can then drill down with a module name or command name, for example `%help administration` or `%help rolesetting`.")]
    public Task HelpAsync([CommandParameter(Name = "page-or-topic", Remainder = true)] string? topic = null)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            return SendOverviewPageAsync(1);
        }

        var normalizedTopic = topic.Trim();
        if (TryParsePageQuery(normalizedTopic, out var pageNumber))
        {
            return SendOverviewPageAsync(pageNumber);
        }

        var moduleQuery = normalizedTopic.StartsWith("module ", StringComparison.OrdinalIgnoreCase)
            ? normalizedTopic["module ".Length..].Trim()
            : normalizedTopic;

        if (helpCatalogService.TryGetModule(moduleQuery, out var module))
        {
            return SendModuleHelpAsync(module);
        }

        if (helpCatalogService.TryGetCommand(normalizedTopic, out var command))
        {
            return SendCommandHelpAsync(command);
        }

        return ReplyAsync(new ReplyMessageProperties
        {
            Content = $"I couldn't find help for `{normalizedTopic}`. Try `%help`, `%help administration`, or `%help rolesetting`.",
        });
    }

    private Task SendOverviewPageAsync(int requestedPage)
    {
        var modules = helpCatalogService.GetModules();
        var totalPages = Math.Max(1, (int)Math.Ceiling(modules.Count / (double)ModulesPerPage));
        var pageNumber = Math.Clamp(requestedPage, 1, totalPages);
        var pageModules = modules
            .Skip((pageNumber - 1) * ModulesPerPage)
            .Take(ModulesPerPage)
            .ToArray();

        return SendAsync(new MessageProperties
        {
            Embeds =
            [
                new EmbedProperties
                {
                    Title = "Bean Bot Help",
                    Description = "Use `%`, `succ `, or mention the bot before a command. `%help` shows modules, `%help <module>` shows that module's commands, and `%help <command>` shows command details.",
                    Fields =
                    [
                        .. pageModules.Select(module => new EmbedFieldProperties
                        {
                            Name = module.Name,
                            Value = string.Join(
                                Environment.NewLine,
                                new[]
                                {
                                    module.Summary,
                                    $"Commands: {string.Join(", ", module.Commands.Select(command => $"`{command.Name}`"))}",
                                    $"Drill down: `%help {module.Name.ToLowerInvariant()}`",
                                }),
                        }),
                    ],
                    Footer = new EmbedFooterProperties
                    {
                        Text = $"Page {pageNumber}/{totalPages}",
                    },
                },
            ],
        });
    }

    private Task SendModuleHelpAsync(HelpModuleDescriptor module)
    {
        return SendAsync(new MessageProperties
        {
            Embeds =
            [
                new EmbedProperties
                {
                    Title = $"{module.Name} Module",
                    Description = module.Summary,
                    Fields =
                    [
                        .. module.Commands.Select(command => new EmbedFieldProperties
                        {
                            Name = command.Name,
                            Value = $"{command.Summary}{Environment.NewLine}Usage: `{command.Usage}`{Environment.NewLine}More info: `%help {command.Name}`",
                        }),
                    ],
                    Footer = new EmbedFooterProperties
                    {
                        Text = "Use `%help <command>` to drill down into a specific command.",
                    },
                },
            ],
        });
    }

    private Task SendCommandHelpAsync(HelpCommandDescriptor command)
    {
        var parameterText = command.Parameters.Count == 0
            ? "None"
            : string.Join(
                Environment.NewLine,
                command.Parameters.Select(parameter =>
                    $"{(parameter.IsOptional ? "[" : "<")}{parameter.Name}{(parameter.IsOptional ? "]" : ">")}{(parameter.IsRemainder ? "..." : string.Empty)}"));

        return SendAsync(new MessageProperties
        {
            Embeds =
            [
                new EmbedProperties
                {
                    Title = $"{command.Name} Command",
                    Description = command.Summary,
                    Fields =
                    [
                        new EmbedFieldProperties
                        {
                            Name = "Usage",
                            Value = $"`{command.Usage}`",
                        },
                        new EmbedFieldProperties
                        {
                            Name = "Module",
                            Value = command.ModuleName,
                            Inline = true,
                        },
                        new EmbedFieldProperties
                        {
                            Name = "Scope",
                            Value = command.GuildOnly ? "Guild only" : "Guilds and DMs",
                            Inline = true,
                        },
                        new EmbedFieldProperties
                        {
                            Name = "Aliases",
                            Value = command.Aliases.Count == 0 ? "None" : string.Join(", ", command.Aliases.Select(alias => $"`{alias}`")),
                        },
                        new EmbedFieldProperties
                        {
                            Name = "Parameters",
                            Value = parameterText,
                        },
                    ],
                    Footer = new EmbedFooterProperties
                    {
                        Text = $"Use `%help {command.ModuleName.ToLowerInvariant()}` to return to the module page.",
                    },
                },
            ],
        });
    }

    private static bool TryParsePageQuery(string topic, out int pageNumber)
    {
        if (int.TryParse(topic, out pageNumber))
        {
            return pageNumber > 0;
        }

        const string pagePrefix = "page ";
        if (topic.StartsWith(pagePrefix, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(topic[pagePrefix.Length..].Trim(), out pageNumber))
        {
            return pageNumber > 0;
        }

        pageNumber = 0;
        return false;
    }
}
