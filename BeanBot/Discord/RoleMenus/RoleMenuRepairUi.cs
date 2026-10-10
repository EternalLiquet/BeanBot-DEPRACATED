using System.Globalization;
using BeanBot.Persistence.Models;
using BeanBot.Persistence.Repositories;
using Discord;
using MongoDB.Bson;

namespace BeanBot.Discord.RoleMenus;

internal static class RoleMenuRepairUi
{
    internal const string SelectPattern = "rm:rs:*:*";
    internal const string PagePattern = "rm:rp:*:*:*:*:*";
    internal const string ConfirmPattern = "rm:rc:*:*:*:*";
    internal const string CancelPattern = "role-menu:repair-cancel:*";

    internal static string Select(ulong userId, ulong targetChannelId)
        => EnsureValid($"rm:rs:{userId.ToString(CultureInfo.InvariantCulture)}:" +
                       targetChannelId.ToString(CultureInfo.InvariantCulture));

    internal static string Page(ulong userId, ulong targetChannelId, RoleMenuPageCursor cursor)
        => EnsureValid($"rm:rp:{userId.ToString(CultureInfo.InvariantCulture)}:" +
                       targetChannelId.ToString(CultureInfo.InvariantCulture) + ":" +
                       (cursor.Direction == RoleMenuPageDirection.Newer ? "n" : "o") + ":" +
                       cursor.CreatedAtUtc.Ticks.ToString(CultureInfo.InvariantCulture) + ":" +
                       cursor.MenuId);

    internal static string Confirm(
        ulong userId,
        ObjectId menuId,
        ulong targetChannelId,
        string fingerprint)
        => EnsureValid(
            "rm:rc:" +
            userId.ToString(CultureInfo.InvariantCulture) + ":" +
            menuId + ":" +
            targetChannelId.ToString(CultureInfo.InvariantCulture) + ":" +
            fingerprint);

    internal static string Cancel(ulong userId)
        => EnsureValid(
            "role-menu:repair-cancel:" +
            userId.ToString(CultureInfo.InvariantCulture));

    internal static MessageComponent BuildSelector(
        ulong userId,
        ulong targetChannelId,
        RoleMenuDeletionPage page,
        Func<ulong, string?> getChannelName)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(getChannelName);
        var components = new ComponentBuilder();
        if (page.Menus.Count > 0)
        {
            var selector = new SelectMenuBuilder()
                .WithCustomId(Select(userId, targetChannelId))
                .WithPlaceholder("Choose a role menu to repair")
                .WithMinValues(1)
                .WithMaxValues(1);
            foreach (var menu in page.Menus)
            {
                selector.AddOption(
                    RoleMenuText.TruncateWithEllipsis(
                        RoleMenuComponents.GetDisplayTitle(menu.Title),
                        SelectMenuOptionBuilder.MaxSelectLabelLength),
                    menu.Id.ToString(),
                    RoleMenuText.TruncateWithEllipsis(
                        RoleMenuComponents.DescribeMenuForSelector(menu, getChannelName),
                        SelectMenuOptionBuilder.MaxDescriptionLength));
            }

            components.WithSelectMenu(selector);
        }

        if (page.Previous is { } previous)
        {
            components.WithButton("Previous", Page(userId, targetChannelId, previous),
                ButtonStyle.Secondary, row: 1);
        }

        if (page.Next is { } next)
        {
            components.WithButton("Next", Page(userId, targetChannelId, next),
                ButtonStyle.Secondary, row: 1);
        }

        return components.WithButton("Cancel", Cancel(userId), ButtonStyle.Secondary, row: 1)
            .Build();
    }

    internal static Embed BuildConfirmationEmbed(
        RoleMenuSettings settings,
        IReadOnlyCollection<RoleMenuRoleSnapshot> roles,
        ulong targetChannelId,
        RoleMenuRepairPanelIssue panelIssue)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(roles);
        var roleMentions = string.Join(
            " ",
            roles.Select(role => $"<@&{role.Id.ToString(CultureInfo.InvariantCulture)}>"));
        var mode = settings.SelectionMode == RoleMenuSelectionMode.Exclusive
            ? "Single selection"
            : "Multiple selection";
        var source = panelIssue == RoleMenuRepairPanelIssue.ChannelMissing
            ? "The menu's old channel is gone."
            : "The menu message is gone.";

        return new EmbedBuilder()
            .WithTitle("Repair role menu?")
            .WithDescription(
                source + " I'll post a replacement message for this menu in the target channel.")
            .AddField("Menu", settings.Title)
            .AddField("Roles", roleMentions)
            .AddField("Mode", mode, inline: true)
            .AddField(
                "Target",
                $"<#{targetChannelId.ToString(CultureInfo.InvariantCulture)}>",
                inline: true)
            .Build();
    }

    internal static MessageComponent BuildConfirmationComponents(
        ulong userId,
        RoleMenuSettings settings,
        ulong targetChannelId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new ComponentBuilder()
            .WithButton(
                "Repair",
                Confirm(userId, settings.Id, targetChannelId,
                    RoleMenuRepairWorkflow.GetPreviewFingerprint(settings)),
                ButtonStyle.Primary)
            .WithButton(
                "Cancel",
                Cancel(userId),
                ButtonStyle.Secondary)
            .Build();
    }

    private static string EnsureValid(string customId)
    {
        if (customId.Length > ComponentBuilder.MaxCustomIdLength)
        {
            throw new InvalidOperationException(
                $"Role-menu repair custom ID exceeded {ComponentBuilder.MaxCustomIdLength} characters.");
        }

        return customId;
    }
}
