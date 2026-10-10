using System.Globalization;
using BeanBot.Persistence.Models;
using BeanBot.Persistence.Repositories;
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

        var roleMentions = FormatRoleMentions(roles);
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

    internal static Embed BuildMigrationPreviewEmbed(
        RoleMenuDraft draft,
        IReadOnlyCollection<RoleMenuRoleSnapshot> roles,
        string sourceMessageLink)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceMessageLink);

        return new EmbedBuilder()
            .WithTitle(draft.Title)
            .WithDescription(string.IsNullOrWhiteSpace(draft.Description)
                ? DefaultDescription
                : draft.Description)
            .AddField("Legacy source", sourceMessageLink)
            .AddField("Roles", FormatRoleMentions(roles))
            .AddField("Mode", "Multiple selection", inline: true)
            .AddField(
                "Target channel",
                $"<#{draft.TargetChannelId.ToString(CultureInfo.InvariantCulture)}>",
                inline: true)
            .AddField(
                "Retirement",
                "The legacy reaction-role panel and saved configuration stay unchanged. " +
                "Retire them manually only after verifying this replacement.")
            .WithFooter("Migration preview • Not published")
            .Build();
    }

    internal static MessageComponent BuildMigrationPreviewComponents(Guid draftId)
        => new ComponentBuilder()
            .WithButton(
                "Publish migration",
                RoleMenuCustomIds.MigrateConfirm(draftId),
                ButtonStyle.Success)
            .WithButton(
                "Cancel",
                RoleMenuCustomIds.MigrateCancel(draftId),
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

    internal static Embed BuildEditSummaryEmbed(RoleMenuEditDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var roleMentions = string.Join(
            " ",
            draft.RoleIds.Select(roleId =>
                $"<@&{roleId.ToString(CultureInfo.InvariantCulture)}>"));
        var mode = draft.SelectionMode == RoleMenuSelectionMode.Exclusive
            ? "Single selection"
            : "Multiple selection";
        return new EmbedBuilder()
            .WithTitle(draft.Title)
            .WithDescription(string.IsNullOrWhiteSpace(draft.Description)
                ? DefaultDescription
                : draft.Description)
            .AddField("Roles", roleMentions)
            .AddField("Mode", mode, inline: true)
            .WithFooter("Current values • Only you can see this")
            .Build();
    }

    internal static MessageComponent BuildEditOpenComponents(Guid draftId)
        => new ComponentBuilder()
            .WithButton(
                "Edit values",
                RoleMenuCustomIds.EditOpen(draftId),
                ButtonStyle.Primary)
            .Build();

    internal static MessageComponent BuildEditSelector(
        ulong userId,
        RoleMenuDeletionPage page,
        Func<ulong, string?> getChannelName,
        DateTime? nowUtc = null)
        => BuildManagementSelector(
            page, getChannelName, RoleMenuCustomIds.EditSelect(userId),
            cursor => RoleMenuCustomIds.EditPage(userId, cursor),
            "Choose a role menu to edit", nowUtc);

    internal static MessageComponent BuildDeleteSelector(
        ulong userId,
        RoleMenuDeletionPage page,
        Func<ulong, string?> getChannelName,
        DateTime? nowUtc = null)
        => BuildManagementSelector(
            page, getChannelName, RoleMenuCustomIds.DeleteSelect(userId),
            cursor => RoleMenuCustomIds.DeletePage(userId, cursor),
            "Choose a role menu", nowUtc, RoleMenuCustomIds.DeleteCancel(userId));

    private static MessageComponent BuildManagementSelector(
        RoleMenuDeletionPage page,
        Func<ulong, string?> getChannelName,
        string selectId,
        Func<RoleMenuPageCursor, string> pageId,
        string placeholder,
        DateTime? nowUtc,
        string? cancelId = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(getChannelName);
        var renderedAtUtc = nowUtc ?? DateTime.UtcNow;
        var components = new ComponentBuilder();
        if (page.Menus.Count > 0)
        {
            var selector = new SelectMenuBuilder()
                .WithCustomId(selectId)
                .WithPlaceholder(placeholder)
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
                        DescribeMenuForSelector(menu, getChannelName, renderedAtUtc),
                        SelectMenuOptionBuilder.MaxDescriptionLength));
            }
            components.WithSelectMenu(selector);
        }
        if (page.Previous is { } previous)
            components.WithButton("Previous", pageId(previous), ButtonStyle.Secondary, row: 1);
        if (page.Next is { } next)
            components.WithButton("Next", pageId(next), ButtonStyle.Secondary, row: 1);
        if (cancelId is not null)
            components.WithButton("Cancel", cancelId, ButtonStyle.Secondary, row: 1);
        return components.Build();
    }

    internal static Embed BuildDeleteConfirmationEmbed(
        RoleMenuSettings settings,
        RoleMenuPanelState panelState,
        DateTime? nowUtc = null)
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
        lines.Add(FormatCreatedAt(settings.CreatedAtUtc, nowUtc ?? DateTime.UtcNow,
            includeDetail: true));
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

    internal static string FormatCreatedAt(
        DateTime createdAtUtc,
        DateTime? nowUtc = null,
        bool includeDetail = false)
    {
        var renderedAtUtc = nowUtc ?? DateTime.UtcNow;
        if (createdAtUtc == default || createdAtUtc > renderedAtUtc)
        {
            return "Creation date unknown";
        }

        var age = renderedAtUtc - createdAtUtc;
        if (age < TimeSpan.FromMinutes(1))
        {
            return "Created just now";
        }

        if (includeDetail)
        {
            var days = age.Days;
            var parts = new List<string>();
            AddAgePart(parts, days / 365, "year");
            AddAgePart(parts, days % 365, "day");
            AddAgePart(parts, age.Hours, "hour");
            AddAgePart(parts, age.Minutes, "minute");
            return "Created " + string.Join(", ", parts) + " ago";
        }

        long amount;
        string unit;
        if (age < TimeSpan.FromHours(1))
        {
            amount = (long)age.TotalMinutes;
            unit = "minute";
        }
        else if (age < TimeSpan.FromDays(1))
        {
            amount = (long)age.TotalHours;
            unit = "hour";
        }
        else if (age < TimeSpan.FromDays(365))
        {
            amount = (long)age.TotalDays;
            unit = "day";
        }
        else
        {
            amount = (long)(age.TotalDays / 365);
            unit = "year";
        }

        return $"Created {amount.ToString(CultureInfo.InvariantCulture)} " +
               $"{unit}{(amount == 1 ? "" : "s")} ago";
    }

    private static void AddAgePart(List<string> parts, int amount, string unit)
    {
        if (amount > 0)
        {
            parts.Add($"{amount.ToString(CultureInfo.InvariantCulture)} " +
                      $"{unit}{(amount == 1 ? "" : "s")}");
        }
    }

    private static string FormatSelectionMode(RoleMenuSelectionMode selectionMode)
        => selectionMode == RoleMenuSelectionMode.Exclusive
            ? "Members can choose one"
            : "Members can choose any number";

    internal static string DescribeMenuForSelector(
        RoleMenuSettings menu,
        Func<ulong, string?> getChannelName,
        DateTime? nowUtc = null)
    {
        var age = FormatCreatedAt(menu.CreatedAtUtc, nowUtc, includeDetail: true);
        var channelName = RoleMenuCustomIds.TryParseSnowflake(menu.ChannelId, out var channelId)
            ? getChannelName(channelId)
            : null;
        var channel = string.IsNullOrWhiteSpace(channelName)
            ? "Unknown channel"
            : "#" + RoleMenuText.TruncateWithEllipsis(
                channelName,
                Math.Min(40, SelectMenuOptionBuilder.MaxDescriptionLength - age.Length - 4));
        return $"{channel} • {age}";
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

    private static string FormatRoleMentions(IReadOnlyCollection<RoleMenuRoleSnapshot> roles)
    {
        var roleMentions = string.Join(
            " ",
            roles.Select(role => $"<@&{role.Id.ToString(CultureInfo.InvariantCulture)}>"));
        return roleMentions.Length == 0
            ? "None of these roles can be used anymore." : roleMentions;
    }
}
