using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using BeanBot.Logging;
using BeanBot.Persistence.Models;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using static BeanBot.Discord.RoleMenus.DiscordRoleMenuClient;
using static BeanBot.Discord.RoleMenus.RoleMenuPresentation;

namespace BeanBot.Discord.RoleMenus;

internal static class RoleMenuSetupValidation
{
    internal static bool TryParseAndValidateModal(
        RoleMenuCreateRequest request,
        ulong guildId,
        IGuildUser administrator,
        IGuildUser bot,
        string title,
        string description,
        out ulong targetChannelId,
        out RoleMenuSelectionMode selectionMode,
        [NotNullWhen(true)] out RoleMenuRoleValidationResult? roleValidation,
        out string validationMessage)
    {
        targetChannelId = 0;
        selectionMode = default;
        roleValidation = null;
        if (!RoleMenuText.HasVisibleText(title))
        {
            validationMessage = "Give the menu a title.";
            return false;
        }

        if (title.Length > RoleMenuConstants.MaximumTitleLength)
        {
            validationMessage =
                $"The title can't be longer than {RoleMenuConstants.MaximumTitleLength} characters.";
            return false;
        }

        if (description.Length > RoleMenuConstants.MaximumDescriptionLength)
        {
            validationMessage =
                $"The description can't be longer than {RoleMenuConstants.MaximumDescriptionLength} characters.";
            return false;
        }

        if (!TryParseSelectionMode(request.SelectionMode, out selectionMode))
        {
            validationMessage = "Choose how many roles members can pick.";
            return false;
        }

        if (request.TargetChannelId is null
            || request.TargetChannelGuildId != guildId
            || request.TargetChannelType != ChannelType.Text)
        {
            validationMessage = "Choose a text channel in this server.";
            return false;
        }

        targetChannelId = request.TargetChannelId.Value;
        if (request.RoleIds is not { Count: >= 1 and <= RoleMenuConstants.MaximumRoles })
        {
            validationMessage =
                $"Choose between 1 and {RoleMenuConstants.MaximumRoles} roles.";
            return false;
        }

        roleValidation = ValidateRoles(
            request.RoleIds,
            administrator,
            bot);
        if (!roleValidation.IsValid)
        {
            validationMessage = FormatRoleValidationFailure(roleValidation);
            return false;
        }

        validationMessage = string.Empty;
        return true;
    }

    internal static bool TryParseSelectionMode(
        string value,
        out RoleMenuSelectionMode selectionMode)
    {
        if (string.Equals(value, "multiple", StringComparison.Ordinal))
        {
            selectionMode = RoleMenuSelectionMode.Multiple;
            return true;
        }

        if (string.Equals(value, "single", StringComparison.Ordinal))
        {
            selectionMode = RoleMenuSelectionMode.Exclusive;
            return true;
        }

        selectionMode = default;
        return false;
    }

    internal static RoleMenuRoleValidationResult ValidateDraftRoles(
        RoleMenuDraft draft,
        IGuildUser administrator,
        IGuildUser bot)
        => ValidateRoles(draft.RoleIds, administrator, bot);

    internal static RoleMenuRoleValidationResult ValidateRoles(
        IReadOnlyCollection<ulong> roleIds,
        IGuildUser administrator,
        IGuildUser bot)
    {
        var availableRoles = bot.Guild.Roles
            .Select(role => new RoleMenuRoleSnapshot(
                role.Id,
                role.Name,
                role.Id == bot.Guild.EveryoneRole.Id,
                role.IsManaged,
                role.Position))
            .ToList();
        return RoleMenuRoleValidator.Validate(
            roleIds,
            availableRoles,
            CreateActorSnapshot(bot),
            CreateActorSnapshot(administrator));
    }

    internal static string? GetChannelPermissionFailure(
        IGuildUser bot,
        ITextChannel targetChannel)
    {
        if (!bot.GuildPermissions.ManageRoles)
        {
            return "I need the **Manage Roles** permission to publish this menu.";
        }

        var permissions = bot.GetPermissions(targetChannel);
        var missing = new List<string>();
        if (!permissions.ViewChannel)
        {
            missing.Add("View Channel");
        }

        if (!permissions.SendMessages)
        {
            missing.Add("Send Messages");
        }

        if (!permissions.EmbedLinks)
        {
            missing.Add("Embed Links");
        }

        if (!permissions.ReadMessageHistory)
        {
            missing.Add("Read Message History");
        }

        return missing.Count == 0
            ? null
            : "I'm missing these permissions in that channel: **" +
              string.Join(", ", missing) + "**. Add them, then try again.";
    }

    internal static RoleMenuRoleValidationResult ValidateRoles(
        IReadOnlyCollection<ulong> roleIds,
        IGuildUser bot)
    {
        return RoleMenuRoleValidator.Validate(
            roleIds,
            CreateRoleSnapshots(bot),
            CreateActorSnapshot(bot));
    }

}
