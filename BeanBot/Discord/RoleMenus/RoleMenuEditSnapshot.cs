using BeanBot.Persistence.Models;
using MongoDB.Bson;

namespace BeanBot.Discord.RoleMenus;

/// <summary>The complete saved state shown to an administrator before an edit.</summary>
internal sealed record RoleMenuEditSnapshot(
    ObjectId MenuId,
    string GuildId,
    string ChannelId,
    string MessageId,
    string Title,
    string Description,
    IReadOnlyList<string> RoleIds,
    RoleMenuSelectionMode SelectionMode,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc)
{
    internal static RoleMenuEditSnapshot From(RoleMenuSettings settings)
        => new(settings.Id, settings.GuildId, settings.ChannelId, settings.MessageId,
            settings.Title, settings.Description, [.. settings.RoleIds], settings.SelectionMode,
            settings.CreatedAtUtc, settings.UpdatedAtUtc);

    internal bool Matches(RoleMenuSettings settings)
        => MenuId == settings.Id
           && GuildId == settings.GuildId
           && ChannelId == settings.ChannelId
           && MessageId == settings.MessageId
           && Title == settings.Title
           && Description == settings.Description
           && RoleIds.SequenceEqual(settings.RoleIds)
           && SelectionMode == settings.SelectionMode
           && CreatedAtUtc == settings.CreatedAtUtc
           && UpdatedAtUtc == settings.UpdatedAtUtc;
}
