using System.Reflection;
using BeanBot.Discord.RoleMenus;
using BeanBot.Persistence.Models;
using Discord;
using MongoDB.Bson;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public sealed class RoleMenuPanelIdentityTests
{
    [Fact]
    public void Matches_AcceptsSavedBeanBotPanelAndExistingManageControl()
    {
        var settings = CreateSettings();
        var message = CreateMessage(RoleMenuComponents.BuildPublicComponents(settings.Id));

        Assert.True(RoleMenuPanelIdentity.Matches(settings, 1, 2, 3, 4, 4, message));
    }

    [Theory]
    [InlineData(9UL, 2UL, 3UL, 4UL)]
    [InlineData(1UL, 9UL, 3UL, 4UL)]
    [InlineData(1UL, 2UL, 9UL, 4UL)]
    [InlineData(1UL, 2UL, 3UL, 9UL)]
    public void Matches_RejectsWrongIdentityOrAuthor(
        ulong guildId, ulong channelId, ulong messageId, ulong authorId)
    {
        var settings = CreateSettings();
        var message = CreateMessage(RoleMenuComponents.BuildPublicComponents(settings.Id));

        Assert.False(RoleMenuPanelIdentity.Matches(
            settings, guildId, channelId, messageId, authorId, 4, message));
    }

    [Fact]
    public void Matches_RejectsMissingSettingsAndArbitraryBeanBotMessage()
    {
        var settings = CreateSettings();
        var message = CreateMessage(MessageComponent.Empty);

        Assert.False(RoleMenuPanelIdentity.Matches(null, 1, 2, 3, 4, 4, message));
        Assert.False(RoleMenuPanelIdentity.Matches(settings, 1, 2, 3, 4, 4, message));
    }

    [Fact]
    public void MatchesConfirmation_RechecksPersistedAndLivePanelIdentity()
    {
        var settings = CreateSettings();
        var valid = new RoleMenuPanelLookupResult(
            RoleMenuPanelLookupStatus.Found,
            new RoleMenuPanelSnapshot(1, 2, 3, 4, true));

        Assert.True(RoleMenuPanelIdentity.MatchesConfirmation(settings, 1, 2, 3, 4, valid));
        Assert.False(RoleMenuPanelIdentity.MatchesConfirmation(settings, 1, 2, 9, 4, valid));
        Assert.False(RoleMenuPanelIdentity.MatchesConfirmation(settings, 1, 2, 3, 9, valid));
        Assert.False(RoleMenuPanelIdentity.MatchesConfirmation(settings, 1, 2, 3, 4,
            valid with { Panel = valid.Panel! with { HasManageButton = false } }));
        Assert.False(RoleMenuPanelIdentity.MatchesConfirmation(settings, 1, 2, 3, 4,
            new RoleMenuPanelLookupResult(RoleMenuPanelLookupStatus.MessageMissing)));
        Assert.False(RoleMenuPanelIdentity.MatchesConfirmation(null, 1, 2, 3, 4, valid));
    }

    private static RoleMenuSettings CreateSettings()
        => new(ObjectId.GenerateNewId(), "1", "2", "3", "Games", "", ["5"],
            RoleMenuSelectionMode.Multiple);

    private static IMessage CreateMessage(MessageComponent components)
    {
        var message = DispatchProxy.Create<IMessage, MessageProxy>();
        ((MessageProxy)(object)message).Components = components.Components;
        return message;
    }

    public class MessageProxy : DispatchProxy
    {
        public IReadOnlyCollection<IMessageComponent> Components { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == "get_Components"
                ? Components
                : throw new NotSupportedException(targetMethod?.Name);
    }
}
