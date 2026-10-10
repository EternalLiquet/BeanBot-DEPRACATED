using System.Globalization;
using BeanBot.Persistence.Models;
using Discord;
using MongoDB.Bson;

namespace BeanBot.Discord.RoleMenus;

internal static class LegacyReactionRoleRetirementPicker
{
    internal const int PageSize = 25;
    internal const string SelectPattern = "rm:ls:*";
    internal const string PagePattern = "rm:lp:*:*:*";

    internal static string Select(ulong userId) => $"rm:ls:{userId}";

    internal static string Page(ulong userId, bool newer, ObjectId cursor)
        => $"rm:lp:{userId}:{(newer ? "n" : "o")}:{cursor}";

    internal static bool TryParseCursor(string direction, string id, out bool newer,
        out ObjectId cursor)
    {
        newer = direction == "n";
        cursor = ObjectId.Empty;
        return (newer || direction == "o")
            && ObjectId.TryParse(id, out cursor)
            && cursor != ObjectId.Empty;
    }

    internal static MessageComponent Build(
        ulong userId,
        IReadOnlyList<ReactionRoleSettings> fetched,
        ObjectId? requestCursor,
        bool newer,
        Func<ulong, string?> channelName,
        Func<ulong, string?> roleName,
        DateTime? nowUtc = null,
        string? selectId = null,
        Func<ulong, bool, ObjectId, string>? pageId = null)
    {
        ArgumentNullException.ThrowIfNull(fetched);
        ArgumentNullException.ThrowIfNull(channelName);
        ArgumentNullException.ThrowIfNull(roleName);
        var page = fetched.Skip(newer && fetched.Count > PageSize ? 1 : 0)
            .Take(PageSize).ToList();
        var components = new ComponentBuilder();
        if (page.Count > 0)
        {
            var selector = new SelectMenuBuilder()
                .WithCustomId(selectId ?? Select(userId))
                .WithPlaceholder("Choose a legacy panel")
                .WithMinValues(1)
                .WithMaxValues(1);
            var addedOptions = 0;
            foreach (var setting in page)
            {
                if (!ulong.TryParse(setting.MessageId, NumberStyles.None,
                        CultureInfo.InvariantCulture, out var messageId))
                {
                    continue;
                }
                var channel = ulong.TryParse(setting.ChannelId, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var channelId)
                    ? channelName(channelId) : null;
                var firstRole = setting.RoleEmotePairs
                    .Select(pair => ulong.TryParse(pair.RoleId, NumberStyles.None,
                        CultureInfo.InvariantCulture, out var roleId)
                        ? roleName(roleId) : null)
                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
                var postedAt = DateTimeOffset.FromUnixTimeMilliseconds(
                    (long)(messageId >> 22) + 1_420_070_400_000L);
                var age = RoleMenuComponents.FormatCreatedAt(
                    postedAt.UtcDateTime, nowUtc);
                selector.AddOption(
                    RoleMenuText.TruncateWithEllipsis(
                        channel is null ? "Panel in an unavailable channel" : $"Panel in #{channel}",
                        SelectMenuOptionBuilder.MaxSelectLabelLength),
                    messageId.ToString(CultureInfo.InvariantCulture),
                    RoleMenuText.TruncateWithEllipsis(
                        firstRole is null ? $"{age} · Saved legacy role panel" :
                            $"{age} · Includes {firstRole} · {setting.RoleEmotePairs.Count} roles",
                        SelectMenuOptionBuilder.MaxDescriptionLength));
                addedOptions++;
            }
            if (addedOptions > 0)
            {
                components.WithSelectMenu(selector);
            }
        }
        if (page.Count > 0)
        {
            if (newer ? fetched.Count > PageSize : requestCursor is not null)
            {
                components.WithButton("Previous", (pageId ?? Page)(userId, true, page[0].Id),
                    ButtonStyle.Secondary, row: 1);
            }
            if (newer ? requestCursor is not null : fetched.Count > PageSize)
            {
                components.WithButton("Next", (pageId ?? Page)(userId, false, page[^1].Id),
                    ButtonStyle.Secondary, row: 1);
            }
        }
        return components.Build();
    }
}

internal static class LegacyReactionRoleMigrationPicker
{
    internal const string SelectPattern = "rm:ms:*";
    internal const string PagePattern = "rm:mp:*:*:*";

    internal static string Select(ulong userId) => $"rm:ms:{userId}";

    internal static string Page(ulong userId, bool newer, ObjectId cursor)
        => $"rm:mp:{userId}:{(newer ? "n" : "o")}:{cursor}";
}
