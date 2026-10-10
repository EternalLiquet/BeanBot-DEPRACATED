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
        "Choose the roles you want. You can change them any time.";
    private const string UntitledMenuName = "Untitled role menu";

    internal static Embed BuildPublicEmbed(
        string title,
        string description,
        RoleMenuSelectionMode selectionMode)
    {
        var modeText = selectionMode == RoleMenuSelectionMode.Exclusive
            ? "Choose one role"
            : "Choose as many as you like";
        return new EmbedBuilder()
            .WithTitle(title)
            .WithDescription(string.IsNullOrWhiteSpace(description)
                ? DefaultDescription
                : description)
            .WithFooter($"Role menu • {modeText}")
            .Build();
    }

    internal static MessageComponent BuildPublicComponents(ObjectId menuId)
        => new ComponentBuilder()
            .WithButton(
                "Choose your roles",
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
            roleMentions = "None of these roles can be used anymore.";
        }
        var selectionMode = draft.SelectionMode == RoleMenuSelectionMode.Exclusive
            ? "One role"
            : "Any number";
        return new EmbedBuilder()
            .WithTitle(draft.Title)
            .WithDescription(string.IsNullOrWhiteSpace(draft.Description)
                ? DefaultDescription
                : draft.Description)
            .AddField("Roles", roleMentions)
            .AddField("Members can choose", selectionMode, inline: true)
            .AddField(
                "Channel",
                $"<#{draft.TargetChannelId.ToString(CultureInfo.InvariantCulture)}>",
                inline: true)
            .WithFooter("Preview • Only you can see this")
            .Build();
    }

    internal static MessageComponent BuildPreviewComponents(Guid draftId)
        => new ComponentBuilder()
            .WithButton(
                "Publish menu",
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
                    "Remove my roles from this menu",
                    RoleMenuCustomIds.Clear(settings.Id, userId, parsed.MessageId),
                    ButtonStyle.Secondary,
                    row: 1)
                .Build(),
            conflictingSingleSelection);
    }

    internal static MessageComponent BuildDeleteSelector(
        ulong userId,
        RoleMenuDeletionPage page,
        Func<ulong, string?> getChannelName)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(getChannelName);
        var components = new ComponentBuilder();
        if (page.Menus.Count > 0)
        {
            var selector = new SelectMenuBuilder()
                .WithCustomId(RoleMenuCustomIds.DeleteSelect(userId))
                .WithPlaceholder("Choose a role menu")
                .WithMinValues(1)
                .WithMaxValues(1);
            foreach (var menu in page.Menus)
            {
                selector.AddOption(
                    RoleMenuText.TruncateWithEllipsis(
                        GetDisplayTitle(menu.Title),
                        SelectMenuOptionBuilder.MaxSelectLabelLength),
                    menu.Id.ToString(),
                    RoleMenuText.TruncateWithEllipsis(
                        DescribeMenuForSelector(menu, getChannelName),
                        SelectMenuOptionBuilder.MaxDescriptionLength));
            }

            components.WithSelectMenu(selector);
        }

        if (page.Previous is { } previous)
        {
            components.WithButton(
                "Previous",
                RoleMenuCustomIds.DeletePage(userId, previous),
                ButtonStyle.Secondary,
                row: 1);
        }

        if (page.Next is { } next)
        {
            components.WithButton(
                "Next",
                RoleMenuCustomIds.DeletePage(userId, next),
                ButtonStyle.Secondary,
                row: 1);
        }

        return components
            .WithButton(
                "Cancel",
                RoleMenuCustomIds.DeleteCancel(userId),
                ButtonStyle.Secondary,
                row: 1)
            .Build();
    }

    internal static Embed BuildDeleteConfirmationEmbed(
        RoleMenuSettings settings,
        RoleMenuPanelState panelState)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var title = RoleMenuText.TruncateWithEllipsis(
            GetDisplayTitle(settings.Title),
            RoleMenuConstants.MaximumTitleLength);
        var lines = new List<string>();
        if (RoleMenuCustomIds.TryParseSnowflake(settings.ChannelId, out var channelId))
        {
            lines.Add($"In <#{channelId.ToString(CultureInfo.InvariantCulture)}>");
        }

        lines.Add(
            $"{FormatChoosableRoleCount(settings.RoleIds.Count)} • " +
            FormatSelectionMode(settings.SelectionMode));
        lines.Add(FormatCreatedAt(settings.CreatedAtUtc));
        var panelNote = panelState switch
        {
            RoleMenuPanelState.MessageMissing => "The menu's message was deleted.",
            RoleMenuPanelState.ChannelMissing => "The menu's channel was deleted.",
            RoleMenuPanelState.NotAPanel =>
                "The saved message isn't this role menu anymore, so I'll leave it alone.",
            _ => null
        };
        if (panelNote is not null)
        {
            lines.Add(string.Empty);
            lines.Add(panelNote);
        }

        if (RoleMenuDeletionTargets.CanDelete(panelState))
        {
            lines.Add(string.Empty);
            lines.Add("Members keep the roles they already have.");
        }

        return new EmbedBuilder()
            .WithTitle(title)
            .WithDescription(string.Join('\n', lines))
            .WithColor(Color.Red)
            .Build();
    }

    internal static string FormatDeleteConfirmationContent(RoleMenuPanelState panelState)
        => panelState switch
        {
            RoleMenuPanelState.Inaccessible =>
                "I can't open this menu's message. Make sure I can see that channel and read its " +
                "history, then try again.",
            RoleMenuPanelState.Unavailable =>
                "I couldn't check this menu's message. Try again in a moment.",
            _ => "Delete this role menu?"
        };

    internal static string GetDisplayTitle(string? title)
        => RoleMenuText.HasVisibleText(title) ? title!.Trim() : UntitledMenuName;

    internal static string FormatCreatedAt(DateTime createdAtUtc)
        => createdAtUtc == default
            ? "Creation date unknown"
            : "Created " +
              createdAtUtc.ToString("MMM d, yyyy, h:mm tt", CultureInfo.InvariantCulture) +
              " UTC";

    private static string FormatSelectionMode(RoleMenuSelectionMode selectionMode)
        => selectionMode == RoleMenuSelectionMode.Exclusive
            ? "Members can choose one"
            : "Members can choose any number";

    internal static string DescribeMenuForSelector(
        RoleMenuSettings menu,
        Func<ulong, string?> getChannelName)
    {
        var channelName = RoleMenuCustomIds.TryParseSnowflake(menu.ChannelId, out var channelId)
            ? getChannelName(channelId)
            : null;
        var channel = string.IsNullOrWhiteSpace(channelName)
            ? "Unknown channel"
            : "#" + RoleMenuText.TruncateWithEllipsis(channelName, 40);
        return $"{channel} • {FormatCreatedAt(menu.CreatedAtUtc)}";
    }

    internal static string FormatChoosableRoleCount(int roleCount)
        => roleCount switch
        {
            <= 0 => "No roles members can choose",
            1 => "1 role members can choose",
            _ => $"{roleCount.ToString(CultureInfo.InvariantCulture)} roles members can choose"
        };

    internal static MessageComponent BuildDeleteConfirmationComponents(
        ulong userId,
        RoleMenuSettings settings,
        RoleMenuPanelState panelState)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!RoleMenuDeletionTargets.CanDelete(panelState))
        {
            return MessageComponent.Empty;
        }

        var components = new ComponentBuilder()
            .WithButton(
                "Delete menu",
                RoleMenuCustomIds.DeleteConfirm(
                    userId,
                    settings.Id,
                    RoleMenuDeletionTargets.GetVersion(settings)),
                ButtonStyle.Danger)
            .WithButton(
                "Cancel",
                RoleMenuCustomIds.DeleteCancel(userId),
                ButtonStyle.Secondary);
        if (panelState == RoleMenuPanelState.Current
            && TryCreatePanelUrl(settings, out var panelUrl))
        {
            components.WithButton("View menu", style: ButtonStyle.Link, url: panelUrl);
        }

        return components.Build();
    }

    internal static MessageComponent BuildViewMenuLink(string panelUrl)
        => new ComponentBuilder()
            .WithButton("View menu", style: ButtonStyle.Link, url: panelUrl)
            .Build();

    private static bool TryCreatePanelUrl(RoleMenuSettings settings, out string panelUrl)
    {
        panelUrl = string.Empty;
        if (!RoleMenuCustomIds.TryParseSnowflake(settings.GuildId, out var guildId)
            || !RoleMenuCustomIds.TryParseSnowflake(settings.ChannelId, out var channelId)
            || !RoleMenuCustomIds.TryParseSnowflake(settings.MessageId, out var messageId))
        {
            return false;
        }

        panelUrl = RoleMenuPresentation.CreateMessageUrl(guildId, channelId, messageId);
        return true;
    }

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
