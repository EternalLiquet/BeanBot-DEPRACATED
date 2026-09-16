using System.Globalization;
using BeanBot.Persistence.Models;
using Discord;
using MongoDB.Bson;

namespace BeanBot.Discord.RoleMenus;

internal static class RoleMenuRepairUi
{
    internal const string ConfirmPattern = "role-menu:repair-confirm:*:*:*";
    internal const string CancelPattern = "role-menu:repair-cancel:*";

    internal static string Confirm(ulong userId, ObjectId menuId, ulong targetChannelId)
        => EnsureValid(
            "role-menu:repair-confirm:" +
            userId.ToString(CultureInfo.InvariantCulture) + ":" +
            menuId + ":" +
            targetChannelId.ToString(CultureInfo.InvariantCulture));

    internal static string Cancel(ulong userId)
        => EnsureValid(
            "role-menu:repair-cancel:" +
            userId.ToString(CultureInfo.InvariantCulture));

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
            ? "The saved source channel is missing."
            : "The saved source message is missing.";

        return new EmbedBuilder()
            .WithTitle("Repair role menu?")
            .WithDescription(
                source + " Bean Bot will publish one replacement panel with the same menu ID and " +
                "saved configuration, then update only the saved channel/message binding.")
            .AddField("Menu", settings.Title)
            .AddField("Roles", roleMentions)
            .AddField("Mode", mode, inline: true)
            .AddField(
                "Target",
                $"<#{targetChannelId.ToString(CultureInfo.InvariantCulture)}>",
                inline: true)
            .WithFooter($"Stable menu ID: {settings.Id}")
            .Build();
    }

    internal static MessageComponent BuildConfirmationComponents(
        ulong userId,
        ObjectId menuId,
        ulong targetChannelId)
        => new ComponentBuilder()
            .WithButton(
                "Repair",
                Confirm(userId, menuId, targetChannelId),
                ButtonStyle.Primary)
            .WithButton(
                "Cancel",
                Cancel(userId),
                ButtonStyle.Secondary)
            .Build();

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
