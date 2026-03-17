using BeanBot.Entities;
using BeanBot.Services;
using BeanBot.Util;
using Microsoft.Extensions.Logging;
using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using NetCord.Services;
using NetCord.Services.Commands;
using System.ComponentModel;
using System.Globalization;
using System.Text;

namespace BeanBot.Modules;

[DisplayName("Administration")]
[Description("Guild-only bot management commands.")]
public sealed class AdministrativeModule(
    MessagePromptService messagePromptService,
    RoleReactService roleReactService,
    GuildUserResolverService guildUserResolverService,
    GuildRoleManagementService guildRoleManagementService,
    ILogger<AdministrativeModule> logger) : CommandModule<CommandContext>
{
    private static readonly TimeSpan InteractionTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CleanupDelay = TimeSpan.FromSeconds(5);
    private const string DefaultRoleGroupLabel = "Role Selection";
    private const int MaxEmbedDescriptionLength = 3500;
    private const int MaxEmbedsPerRoleMessage = 10;

    [Command("rolesetting", "rolesettings", "role-setting", "role-settings")]
    [RequireContext<CommandContext>(RequiredContext.Guild)]
    [Description("Creates a reaction-role message from one pasted batch of emoji/role mappings. The bot first asks for a menu label, then asks for a single message with one mapping per line in the form `<emoji> <role mention or exact role name>`. Standard emoji must be sent as the actual emoji character, not a shortcode alias.")]
    public async Task RoleSettingAsync()
    {
        var guild = ResolveGuild();
        var channel = ResolveChannel();
        if (guild is null || channel is null)
        {
            logger.LogWarning(
                "Rejected role-setting command for message {MessageId} because the guild or channel context could not be resolved",
                Context.Message.Id);
            await ReplyAsync(new ReplyMessageProperties
            {
                Content = "This command can only be used inside a guild text channel.",
            });
            return;
        }

        logger.LogInformation(
            "Starting role-setting command for guild {GuildId}, channel {ChannelId}, message {MessageId}, user {UserId}",
            guild.Id,
            channel.Id,
            Context.Message.Id,
            Context.User.Id);

        if (!await EnsureRoleConfigurationPermissionsAsync())
        {
            return;
        }

        var botUser = await guildUserResolverService.ResolveCurrentGuildUserAsync(
            Context.Client,
            guild,
            $"role-setting validation for message {Context.Message.Id}");
        if (botUser is null)
        {
            await ReplyAsync(new ReplyMessageProperties
            {
                Content = "I could not resolve my guild user record to validate role hierarchy.",
            });
            return;
        }

        var interactionMessageIds = new List<ulong> { Context.Message.Id };

        try
        {
            var roleGroupLabel = await PromptForRoleGroupLabelAsync(interactionMessageIds);
            if (roleGroupLabel is null)
            {
                return;
            }

            var configuredMappings = await PromptForRoleMappingsAsync(guild, botUser, interactionMessageIds);
            if (configuredMappings is null || configuredMappings.Count == 0)
            {
                return;
            }

            var message = await CreateRoleSettingsMessageAsync(configuredMappings, roleGroupLabel);
            if (message is null)
            {
                return;
            }

            await roleReactService.SaveRoleSettingsAsync(
                configuredMappings.Select(configuration => configuration.ToRoleEmotePair()),
                guild.Id,
                channel.Id,
                message.Id);

            logger.LogInformation(
                "Saved role settings for guild {GuildId}, channel {ChannelId}, source message {SourceMessageId}, settings message {SettingsMessageId}",
                guild.Id,
                channel.Id,
                Context.Message.Id,
                message.Id);
        }
        finally
        {
            await CleanupInteractionMessagesAsync(interactionMessageIds);
        }
    }

    private async Task<bool> EnsureRoleConfigurationPermissionsAsync()
    {
        var guild = ResolveGuild();
        var channel = ResolveChannel();
        if (guild is null || channel is null)
        {
            logger.LogWarning(
                "Unable to validate role-setting permissions for message {MessageId} because the guild or channel context could not be resolved",
                Context.Message.Id);
            await ReplyAsync(new ReplyMessageProperties
            {
                Content = "This command can only be used inside a guild.",
            });
            return false;
        }

        var permissionCheckContext = $"role-setting permission check for message {Context.Message.Id}";
        var invokingUser = await guildUserResolverService.ResolveGuildUserAsync(
            Context.Client,
            guild,
            Context.User.Id,
            permissionCheckContext);
        if (invokingUser is null)
        {
            logger.LogWarning(
                "Unable to resolve invoking guild user {UserId} in guild {GuildId} for message {MessageId}",
                Context.User.Id,
                guild.Id,
                Context.Message.Id);
            await ReplyAsync(new ReplyMessageProperties
            {
                Content = "I couldn't resolve your guild member record yet. Please try again in a moment.",
            });
            return false;
        }

        var invokingUserPermissions = invokingUser.GetChannelPermissions(guild, channel.Id);
        if (!invokingUserPermissions.HasFlag(Permissions.ManageRoles))
        {
            logger.LogInformation(
                "Rejected role-setting command for user {UserId} in guild {GuildId} because ManageRoles is missing. Permissions={Permissions}",
                invokingUser.Id,
                guild.Id,
                invokingUserPermissions);
            await ReplyAsync(new ReplyMessageProperties
            {
                Content = "You need the Manage Roles permission to configure reaction roles.",
            });
            return false;
        }

        var botUser = await guildUserResolverService.ResolveCurrentGuildUserAsync(
            Context.Client,
            guild,
            permissionCheckContext);
        if (botUser is null)
        {
            logger.LogWarning(
                "Unable to resolve bot guild user {UserId} in guild {GuildId} for message {MessageId}",
                Context.Client.Id,
                guild.Id,
                Context.Message.Id);
            await ReplyAsync(new ReplyMessageProperties
            {
                Content = "I could not resolve my guild user record to validate permissions.",
            });
            return false;
        }

        var botPermissions = botUser.GetChannelPermissions(guild, channel.Id);
        var requiredBotPermissions = Permissions.SendMessages | Permissions.EmbedLinks | Permissions.AddReactions | Permissions.ManageMessages;
        if ((botPermissions & requiredBotPermissions) != requiredBotPermissions)
        {
            logger.LogInformation(
                "Rejected role-setting command in guild {GuildId} because the bot is missing required permissions. Permissions={Permissions} RequiredPermissions={RequiredPermissions}",
                guild.Id,
                botPermissions,
                requiredBotPermissions);
            await ReplyAsync(new ReplyMessageProperties
            {
                Content = "I need Send Messages, Embed Links, Add Reactions, and Manage Messages in this channel to configure reaction roles.",
            });
            return false;
        }

        logger.LogDebug(
            "Role-setting permission validation succeeded for guild {GuildId}, channel {ChannelId}, message {MessageId}",
            guild.Id,
            channel.Id,
            Context.Message.Id);
        return true;
    }

    private async Task<string?> PromptForRoleGroupLabelAsync(List<ulong> interactionMessageIds)
    {
        interactionMessageIds.Add((await ReplyAsync(new ReplyMessageProperties
        {
            Content = "Send a label for this reaction-role menu, or type `skip` to use `Role Selection`. Only your next message in this channel counts, and the prompt stays open for 5 minutes.",
        })).Id);

        var labelMessage = await messagePromptService.WaitForNextMessageAsync(Context, InteractionTimeout);
        if (labelMessage is null)
        {
            interactionMessageIds.Add((await ReplyAsync(new ReplyMessageProperties
            {
                Content = "Time has expired, please try again.",
            })).Id);
            return null;
        }

        interactionMessageIds.Add(labelMessage.Id);

        var label = labelMessage.Content.Trim();
        return string.IsNullOrWhiteSpace(label) || string.Equals(label, "skip", StringComparison.OrdinalIgnoreCase)
            ? DefaultRoleGroupLabel
            : label;
    }

    private async Task<IReadOnlyList<ConfiguredRoleReaction>?> PromptForRoleMappingsAsync(Guild guild, GuildUser botUser, List<ulong> interactionMessageIds)
    {
        interactionMessageIds.Add((await ReplyAsync(new ReplyMessageProperties
        {
            Content = string.Join(Environment.NewLine,
            [
                "Send all role mappings in a single message, one per line, using:",
                "`<emoji> <role mention or exact role name>`",
                string.Empty,
                "Examples:",
                "`<standard emoji> @Announcements`",
                "`<standard emoji> Raid Team`",
                "`<:party:123456789012345678> @Events`",
                string.Empty,
                "For standard emoji, send the actual emoji character. Shortcodes like `:heart:` are not expanded here.",
                "Only your next message in this channel counts, and the prompt stays open for 5 minutes.",
            ]),
        })).Id);

        var mappingsMessage = await messagePromptService.WaitForNextMessageAsync(Context, InteractionTimeout);
        if (mappingsMessage is null)
        {
            interactionMessageIds.Add((await ReplyAsync(new ReplyMessageProperties
            {
                Content = "Time has expired, please try again.",
            })).Id);
            return null;
        }

        interactionMessageIds.Add(mappingsMessage.Id);

        if (!TryParseRoleMappings(guild, botUser, mappingsMessage.Content, out var configuredMappings, out var parsingFailure))
        {
            interactionMessageIds.Add((await ReplyAsync(new ReplyMessageProperties
            {
                Content = parsingFailure,
            })).Id);
            return null;
        }

        return configuredMappings;
    }

    private bool TryParseRoleMappings(Guild guild, GuildUser botUser, string content, out IReadOnlyList<ConfiguredRoleReaction> configuredMappings, out string failureMessage)
    {
        configuredMappings = [];
        failureMessage = string.Empty;

        var lines = content
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        if (lines.Length == 0)
        {
            failureMessage = "I didn't receive any role mappings. Send one mapping per line in the form `<emoji> <role mention or exact role name>`.";
            return false;
        }

        var parsedMappings = new List<ConfiguredRoleReaction>(lines.Length);
        var seenRoleIds = new HashSet<ulong>();
        var seenEmojiKeys = new HashSet<string>(StringComparer.Ordinal);
        var errors = new List<string>();

        for (var index = 0; index < lines.Length; index++)
        {
            var lineNumber = index + 1;
            var line = lines[index];

            if (!TrySplitRoleMappingLine(line, out var emojiToken, out var roleToken))
            {
                errors.Add($"Line {lineNumber}: use `<emoji> <role mention or exact role name>`.");
                continue;
            }

            if (!TryResolveConfiguredEmoji(guild, emojiToken, out var configuredEmoji, out var emojiFailure))
            {
                errors.Add($"Line {lineNumber}: {emojiFailure}");
                continue;
            }

            var role = ResolveRole(guild, roleToken);
            if (role is null)
            {
                errors.Add($"Line {lineNumber}: role `{roleToken}` was not found.");
                continue;
            }

            if (!guildRoleManagementService.CanManageRole(guild, botUser, role, out var roleValidationFailure))
            {
                errors.Add($"Line {lineNumber}: {roleValidationFailure}");
                continue;
            }

            if (!seenRoleIds.Add(role.Id))
            {
                errors.Add($"Line {lineNumber}: role `{role.Name}` is already configured in this menu.");
                continue;
            }

            if (!seenEmojiKeys.Add(configuredEmoji.Key))
            {
                errors.Add($"Line {lineNumber}: emoji `{configuredEmoji.Display}` is already configured in this menu.");
                continue;
            }

            parsedMappings.Add(new ConfiguredRoleReaction(role, configuredEmoji));
        }

        if (errors.Count > 0)
        {
            var displayedErrors = errors.Take(10).ToList();
            if (errors.Count > displayedErrors.Count)
            {
                displayedErrors.Add($"...and {errors.Count - displayedErrors.Count} more issue(s).");
            }

            failureMessage = string.Join(
                Environment.NewLine,
                ["I couldn't build that role menu:", .. displayedErrors]);
            return false;
        }

        configuredMappings = parsedMappings;
        return true;
    }

    private async Task<RestMessage?> CreateRoleSettingsMessageAsync(IReadOnlyList<ConfiguredRoleReaction> configuredMappings, string roleGroupLabel)
    {
        var channel = ResolveChannel() ?? throw new InvalidOperationException("Role settings require a channel context.");
        var embeds = BuildRoleSettingsEmbeds(configuredMappings, roleGroupLabel);
        if (embeds.Count == 0)
        {
            await ReplyAsync(new ReplyMessageProperties
            {
                Content = "That role menu did not contain any valid mappings to publish.",
            });
            return null;
        }

        if (embeds.Count > MaxEmbedsPerRoleMessage)
        {
            await ReplyAsync(new ReplyMessageProperties
            {
                Content = "That role menu is too large for a single Discord message. Split it into smaller groups and try again.",
            });
            return null;
        }

        var message = await SendAsync(new MessageProperties
        {
            Embeds = [.. embeds],
        });

        foreach (var configuredMapping in configuredMappings)
        {
            await channel.AddMessageReactionAsync(message.Id, configuredMapping.Emoji.ToReactionEmojiProperties());
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        return message;
    }

    private IReadOnlyList<EmbedProperties> BuildRoleSettingsEmbeds(IReadOnlyList<ConfiguredRoleReaction> configuredMappings, string roleGroupLabel)
    {
        var lines = configuredMappings
            .Select(configuration => $"{configuration.Emoji.Display} <@&{configuration.Role.Id}>")
            .ToArray();

        if (lines.Length == 0)
        {
            return [];
        }

        var descriptions = new List<string>();
        var currentDescription = new StringBuilder("React below to add or remove the matching role.");

        foreach (var line in lines)
        {
            var nextLine = $"{Environment.NewLine}{Environment.NewLine}{line}";
            if (currentDescription.Length + nextLine.Length > MaxEmbedDescriptionLength)
            {
                descriptions.Add(currentDescription.ToString());
                currentDescription.Clear();
                currentDescription.Append(line);
            }
            else
            {
                currentDescription.Append(nextLine);
            }
        }

        if (currentDescription.Length > 0)
        {
            descriptions.Add(currentDescription.ToString());
        }

        return descriptions.Select((description, index) => new EmbedProperties
        {
            Title = index == 0 ? roleGroupLabel : $"{roleGroupLabel} (cont.)",
            Description = description,
            Footer = index == descriptions.Count - 1
                ? new EmbedFooterProperties
                {
                    Text = "React again to remove the role.",
                }
                : null,
        }).ToArray();
    }

    private async Task CleanupInteractionMessagesAsync(List<ulong> interactionMessageIds)
    {
        var channel = ResolveChannel();
        if (channel is null)
        {
            return;
        }

        await Task.Delay(CleanupDelay);

        var distinctMessageIds = interactionMessageIds.Distinct().ToArray();
        if (distinctMessageIds.Length == 0)
        {
            return;
        }

        try
        {
            if (distinctMessageIds.Length == 1)
            {
                await channel.DeleteMessageAsync(distinctMessageIds[0]);
            }
            else
            {
                await channel.DeleteMessagesAsync(distinctMessageIds);
            }
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Failed to clean up role-setting interaction messages");
        }
    }

    private static bool TrySplitRoleMappingLine(string line, out string emojiToken, out string roleToken)
    {
        emojiToken = string.Empty;
        roleToken = string.Empty;

        var separatorIndex = line.IndexOfAny([' ', '\t']);
        if (separatorIndex <= 0)
        {
            return false;
        }

        emojiToken = line[..separatorIndex].Trim();
        roleToken = line[(separatorIndex + 1)..].Trim();
        return !string.IsNullOrWhiteSpace(emojiToken) && !string.IsNullOrWhiteSpace(roleToken);
    }

    private static bool TryParseRoleMention(string input, out ulong roleId)
    {
        const string prefix = "<@&";
        roleId = 0;

        if (!input.StartsWith(prefix, StringComparison.Ordinal) || !input.EndsWith('>'))
        {
            return false;
        }

        return ulong.TryParse(input[prefix.Length..^1], out roleId);
    }

    private static bool TryParseEmojiMention(string input, out ulong emojiId)
    {
        emojiId = 0;

        var lastColonIndex = input.LastIndexOf(':');
        if (lastColonIndex < 0 || !input.EndsWith('>'))
        {
            return false;
        }

        return ulong.TryParse(input[(lastColonIndex + 1)..^1], out emojiId);
    }

    private static bool TryParseStandardEmoji(string input, out string standardEmoji)
    {
        standardEmoji = input.Trim();
        if (string.IsNullOrWhiteSpace(standardEmoji) || standardEmoji.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var hasEmojiRune = false;
        foreach (var rune in standardEmoji.EnumerateRunes())
        {
            if (rune.Value is 0x200D or 0xFE0E or 0xFE0F or 0x20E3)
            {
                continue;
            }

            if (rune.Value is >= 0x1F1E6 and <= 0x1F1FF)
            {
                hasEmojiRune = true;
                continue;
            }

            if (rune.Value is >= '0' and <= '9' or '#' or '*')
            {
                continue;
            }

            switch (Rune.GetUnicodeCategory(rune))
            {
                case UnicodeCategory.OtherSymbol:
                case UnicodeCategory.ModifierSymbol:
                case UnicodeCategory.NonSpacingMark:
                case UnicodeCategory.EnclosingMark:
                case UnicodeCategory.Format:
                    hasEmojiRune = true;
                    break;
                default:
                    return false;
            }
        }

        return hasEmojiRune;
    }

    private static bool LooksLikeEmojiShortcode(string input)
        => input.Length > 2 &&
           input.StartsWith(":", StringComparison.Ordinal) &&
           input.EndsWith(":", StringComparison.Ordinal) &&
           input.Count(character => character == ':') == 2;

    private static Role? ResolveRole(Guild guild, string roleInput)
    {
        if (TryParseRoleMention(roleInput, out var mentionedRoleId) &&
            guild.Roles.TryGetValue(mentionedRoleId, out var mentionedRole))
        {
            return mentionedRole;
        }

        return guild.Roles.Values.FirstOrDefault(guildRole =>
            string.Equals(guildRole.Name.Trim(), roleInput.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryResolveConfiguredEmoji(Guild guild, string emojiToken, out ConfiguredReactionEmoji configuredEmoji, out string failureMessage)
    {
        configuredEmoji = default!;
        failureMessage = string.Empty;

        if (TryParseEmojiMention(emojiToken, out var mentionedEmojiId) &&
            guild.Emojis.TryGetValue(mentionedEmojiId, out var mentionedEmoji))
        {
            configuredEmoji = ConfiguredReactionEmoji.FromCustomEmoji(mentionedEmoji);
            return true;
        }

        var emojiName = emojiToken.Trim(':');
        var namedEmoji = guild.Emojis.Values.FirstOrDefault(guildEmoji =>
            string.Equals(guildEmoji.Name, emojiName, StringComparison.OrdinalIgnoreCase));
        if (namedEmoji is not null)
        {
            configuredEmoji = ConfiguredReactionEmoji.FromCustomEmoji(namedEmoji);
            return true;
        }

        if (TryParseStandardEmoji(emojiToken, out var standardEmoji))
        {
            configuredEmoji = ConfiguredReactionEmoji.FromStandardEmoji(standardEmoji);
            return true;
        }

        failureMessage = LooksLikeEmojiShortcode(emojiToken)
            ? $"`{emojiToken}` looks like a shortcode alias. Send the actual emoji character instead."
            : $"emoji `{emojiToken}` is not a custom guild emoji or a valid standard emoji.";
        return false;
    }

    private Guild? ResolveGuild()
    {
        if (Context.Guild is { } contextGuild)
        {
            return contextGuild;
        }

        if (Context.Message.Guild is { } messageGuild)
        {
            return messageGuild;
        }

        if (Context.Message.GuildId is ulong guildId &&
            Context.Client.Cache.Guilds.TryGetValue(guildId, out var cachedGuild))
        {
            return cachedGuild;
        }

        return null;
    }

    private TextChannel? ResolveChannel()
    {
        if (Context.Channel is TextChannel contextChannel)
        {
            return contextChannel;
        }

        var messageChannel = Context.Message.Channel;
        if (messageChannel is TextChannel textMessageChannel)
        {
            return textMessageChannel;
        }

        var guild = ResolveGuild();
        if (guild is not null &&
            messageChannel is not null &&
            guild.Channels.TryGetValue(messageChannel.Id, out var cachedChannel) &&
            cachedChannel is TextChannel textChannel)
        {
            return textChannel;
        }

        return null;
    }

    private sealed record ConfiguredRoleReaction(Role Role, ConfiguredReactionEmoji Emoji)
    {
        public RoleEmotePair ToRoleEmotePair()
            => Emoji.Id is ulong emojiId
                ? new RoleEmotePair(Role.Id, emojiId)
                : new RoleEmotePair(Role.Id, Emoji.Key);
    }

    private sealed record ConfiguredReactionEmoji(string Key, string Display, string ReactionName, ulong? Id)
    {
        public static ConfiguredReactionEmoji FromCustomEmoji(CustomEmoji emoji)
            => new(ReactionEmojiKey.FromCustomEmoji(emoji.Id), emoji.ToString(), emoji.Name, emoji.Id);

        public static ConfiguredReactionEmoji FromStandardEmoji(string emoji)
            => new(ReactionEmojiKey.FromStandardEmoji(emoji), emoji, emoji, null);

        public ReactionEmojiProperties ToReactionEmojiProperties()
            => Id is ulong emojiId
                ? new ReactionEmojiProperties(ReactionName, emojiId)
                : new ReactionEmojiProperties(ReactionName);
    }
}
