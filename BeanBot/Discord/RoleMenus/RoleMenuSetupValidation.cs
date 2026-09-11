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
        if (!TryValidateEditableFields(
                request.Title,
                request.Description,
                request.SelectionMode,
                request.RoleIds,
                administrator,
                bot,
                title,
                description,
                out selectionMode,
                out roleValidation,
                out validationMessage))
        {
            return false;
        }

        if (request.TargetChannelId is null
            || request.TargetChannelGuildId != guildId
            || request.TargetChannelType != ChannelType.Text)
        {
            validationMessage = "Choose a normal text channel from this server.";
            return false;
        }

        targetChannelId = request.TargetChannelId.Value;
        return true;
    }

    internal static bool TryParseAndValidateEdit(
        RoleMenuEditRequest request,
        IGuildUser administrator,
        IGuildUser bot,
        string title,
        string description,
        out RoleMenuSelectionMode selectionMode,
        [NotNullWhen(true)] out RoleMenuRoleValidationResult? roleValidation,
        out string validationMessage)
        => TryValidateEditableFields(
            request.Title,
            request.Description,
            request.SelectionMode,
            request.RoleIds,
            administrator,
            bot,
            title,
            description,
            out selectionMode,
            out roleValidation,
            out validationMessage);

    private static bool TryValidateEditableFields(
        string rawTitle,
        string? rawDescription,
        string rawSelectionMode,
        IReadOnlyCollection<ulong>? roleIds,
        IGuildUser administrator,
        IGuildUser bot,
        string title,
        string description,
        out RoleMenuSelectionMode selectionMode,
        [NotNullWhen(true)] out RoleMenuRoleValidationResult? roleValidation,
        out string validationMessage)
    {
        ArgumentNullException.ThrowIfNull(rawTitle);
        ArgumentNullException.ThrowIfNull(rawSelectionMode);
        selectionMode = default;
        roleValidation = null;
        if (string.IsNullOrWhiteSpace(title)
            || title.Length > RoleMenuConstants.MaximumTitleLength)
        {
            validationMessage =
                $"The panel title must be 1–{RoleMenuConstants.MaximumTitleLength} characters.";
            return false;
        }

        if (description.Length > RoleMenuConstants.MaximumDescriptionLength)
        {
            validationMessage =
                $"The description cannot exceed {RoleMenuConstants.MaximumDescriptionLength} characters.";
            return false;
        }

        if (!TryParseSelectionMode(rawSelectionMode, out selectionMode))
        {
            validationMessage = "Choose either single-selection or multiple-selection mode.";
            return false;
        }

        if (roleIds is not { Count: >= 1 and <= RoleMenuConstants.MaximumRoles })
        {
            validationMessage =
                $"Choose between 1 and {RoleMenuConstants.MaximumRoles} roles.";
            return false;
        }

        roleValidation = ValidateRoles(roleIds, administrator, bot);
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
            return "Bean Bot needs the **Manage Roles** permission before this menu can be published.";
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
            : "Bean Bot is missing these permissions in the target channel: **" +
              string.Join(", ", missing) + "**.";
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
