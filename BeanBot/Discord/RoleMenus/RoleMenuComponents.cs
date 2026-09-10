using System.Globalization;
using BeanBot.Persistence.Models;
using Discord;
using MongoDB.Bson;

namespace BeanBot.Discord.RoleMenus;

internal sealed record RoleMenuMemberSelector(
    MessageComponent Components,
    bool HadConflictingSingleSelection);

internal static class RoleMenuComponents
{
    private const string DefaultDescription =
        "Choose the roles you want. You can update this at any time.";

    internal static Embed BuildPublicEmbed(
        ObjectId menuId,
        string title,
        string description,
        RoleMenuSelectionMode selectionMode)
    {
        var modeText = selectionMode == RoleMenuSelectionMode.Exclusive
            ? "Choose one role"
            : "Choose any combination";
        return new EmbedBuilder()
            .WithTitle(title)
            .WithDescription(string.IsNullOrWhiteSpace(description)
                ? DefaultDescription
                : description)
            .WithFooter($"Role menu • {modeText} • ID: {menuId}")
            .Build();
    }

    internal static MessageComponent BuildPublicComponents(ObjectId menuId)
        => new ComponentBuilder()
            .WithButton(
                "Manage Roles",
                RoleMenuCustomIds.Manage(menuId),
                ButtonStyle.Primary)
            .Build();

    internal static Embed BuildPreviewEmbed(
        RoleMenuDraft draft,
        IReadOnlyCollection<RoleMenuRoleSnapshot> roles)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(roles);

        var roleMentions = string.Join(
            " ",
            roles.Select(role => $"<@&{role.Id.ToString(CultureInfo.InvariantCulture)}>"));
        if (roleMentions.Length == 0)
        {
            roleMentions = "No valid roles remain.";
        }
        var selectionMode = draft.SelectionMode == RoleMenuSelectionMode.Exclusive
            ? "Single selection"
            : "Multiple selection";
        return new EmbedBuilder()
            .WithTitle(draft.Title)
            .WithDescription(string.IsNullOrWhiteSpace(draft.Description)
                ? DefaultDescription
                : draft.Description)
            .AddField("Roles", roleMentions)
            .AddField("Mode", selectionMode, inline: true)
            .AddField(
                "Channel",
                $"<#{draft.TargetChannelId.ToString(CultureInfo.InvariantCulture)}>",
                inline: true)
            .WithFooter("Preview • Not published")
            .Build();
    }

    internal static MessageComponent BuildPreviewComponents(Guid draftId)
        => new ComponentBuilder()
            .WithButton(
                "Publish",
                RoleMenuCustomIds.Publish(draftId),
                ButtonStyle.Success)
            .WithButton(
                "Cancel",
                RoleMenuCustomIds.CancelPublish(draftId),
                ButtonStyle.Secondary)
            .Build();

    internal static RoleMenuMemberSelector BuildMemberSelector(
        RoleMenuSettings settings,
        ParsedRoleMenuSettings parsed,
        IReadOnlyCollection<RoleMenuRoleSnapshot> roles,
        IReadOnlyCollection<ulong> currentRoleIds,
        ulong userId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(currentRoleIds);

        var rolesById = roles.ToDictionary(role => role.Id);
        var configuredRoles = parsed.RoleIds
            .Select(roleId => rolesById[roleId])
            .ToList();
        var currentConfiguredRoleIds = parsed.RoleIds
            .Where(currentRoleIds.Contains)
            .ToList();
        var conflictingSingleSelection = settings.SelectionMode == RoleMenuSelectionMode.Exclusive
            && currentConfiguredRoleIds.Count > 1;
        HashSet<ulong> defaultRoleIds = conflictingSingleSelection
            ? [currentConfiguredRoleIds[0]]
            : [.. currentConfiguredRoleIds];

        var selector = new SelectMenuBuilder()
            .WithCustomId(RoleMenuCustomIds.Save(settings.Id, userId, parsed.MessageId))
            .WithPlaceholder("Choose your roles")
            .WithMinValues(0)
            .WithMaxValues(settings.SelectionMode == RoleMenuSelectionMode.Exclusive
                ? 1
                : configuredRoles.Count);
        foreach (var role in configuredRoles)
        {
            selector.AddOption(
                role.Name,
                role.Id.ToString(CultureInfo.InvariantCulture),
                isDefault: defaultRoleIds.Contains(role.Id));
        }

        return new RoleMenuMemberSelector(
            new ComponentBuilder()
                .WithSelectMenu(selector)
                .WithButton(
                    "Clear menu roles",
                    RoleMenuCustomIds.Clear(settings.Id, userId, parsed.MessageId),
                    ButtonStyle.Secondary,
                    row: 1)
                .Build(),
            conflictingSingleSelection);
    }

    internal static MessageComponent BuildDeleteSelector(
        ulong userId,
        IReadOnlyCollection<RoleMenuSettings> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var selector = new SelectMenuBuilder()
            .WithCustomId(RoleMenuCustomIds.DeleteSelect(userId))
            .WithPlaceholder("Choose a role menu to delete")
            .WithMinValues(1)
            .WithMaxValues(1);
        foreach (var menu in settings)
        {
            var mode = menu.SelectionMode == RoleMenuSelectionMode.Exclusive
                ? "Single selection"
                : "Multiple selection";
            var date = menu.CreatedAtUtc == default
                ? "Unknown creation date"
                : menu.CreatedAtUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            selector.AddOption(
                RoleMenuText.TruncateWithEllipsis(
                    string.IsNullOrWhiteSpace(menu.Title)
                        ? "Untitled or stale role menu"
                        : menu.Title,
                    SelectMenuOptionBuilder.MaxSelectLabelLength),
                menu.Id.ToString(),
                RoleMenuText.TruncateWithEllipsis(
                    $"{mode} • {date}",
                    SelectMenuOptionBuilder.MaxDescriptionLength));
        }

        return new ComponentBuilder().WithSelectMenu(selector).Build();
    }

    internal static Embed BuildDeleteConfirmationEmbed(RoleMenuSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var title = RoleMenuText.TruncateWithEllipsis(
            string.IsNullOrWhiteSpace(settings.Title)
                ? "Untitled or stale role menu"
                : settings.Title,
            RoleMenuConstants.MaximumTitleLength);

        return new EmbedBuilder()
            .WithTitle("Delete role menu?")
            .WithDescription(
                $"**{title}**\n\nThis removes the published panel and its saved configuration.")
            .WithColor(Color.Red)
            .Build();
    }

    internal static MessageComponent BuildDeleteConfirmationComponents(
        ulong userId,
        ObjectId menuId)
        => new ComponentBuilder()
            .WithButton(
                "Delete",
                RoleMenuCustomIds.DeleteConfirm(userId, menuId),
                ButtonStyle.Danger)
            .WithButton(
                "Cancel",
                RoleMenuCustomIds.DeleteCancel(userId),
                ButtonStyle.Secondary)
            .Build();

    internal static bool HasManageButton(IMessage message, ObjectId menuId)
    {
        ArgumentNullException.ThrowIfNull(message);
        var expectedCustomId = RoleMenuCustomIds.Manage(menuId);
        return message.Components
            .OfType<ActionRowComponent>()
            .SelectMany(row => row.Components)
            .OfType<ButtonComponent>()
            .Any(button => string.Equals(
                button.CustomId,
                expectedCustomId,
                StringComparison.Ordinal));
    }
}
