using System.Globalization;
using BeanBot.Discord.RoleMenus;
using BeanBot.Persistence.Models;
using MongoDB.Bson;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class RoleMenuEditMemberRegressionTests
{
    private const ulong GuildId = 101;
    private const ulong ChannelId = 202;
    private const ulong MessageId = 303;
    private const ulong BotUserId = 404;
    private const ulong MemberUserId = 505;
    private const ulong RemainingRoleId = 10;
    private const ulong RemovedRoleId = 11;
    private static readonly ObjectId MenuId =
        ObjectId.Parse("64e7611aaac75f172f0f7890");

    [Fact]
    public async Task ExecuteAsync_StalePreEditSelection_CannotGrantRemovedRole()
    {
        var mutationCount = 0;
        var updatedSettings = new RoleMenuSettings(
            MenuId,
            GuildId.ToString(CultureInfo.InvariantCulture),
            ChannelId.ToString(CultureInfo.InvariantCulture),
            MessageId.ToString(CultureInfo.InvariantCulture),
            "Updated menu",
            string.Empty,
            [RemainingRoleId.ToString(CultureInfo.InvariantCulture)],
            RoleMenuSelectionMode.Multiple);
        var operations = new RoleMenuMemberOperations(
            (_, _, _) => Task.FromResult<RoleMenuSettings?>(updatedSettings),
            (_, _, _, _) => Task.FromResult<RoleMenuPanelSnapshot?>(
                new RoleMenuPanelSnapshot(
                    GuildId,
                    ChannelId,
                    MessageId,
                    BotUserId,
                    HasManageButton: true)),
            (_, _, _) => Task.FromResult<RoleMenuBotSnapshot?>(
                new RoleMenuBotSnapshot(
                    GuildId,
                    BotUserId,
                    [
                        new RoleMenuRoleSnapshot(
                            RemainingRoleId,
                            "Remaining",
                            IsEveryone: false,
                            IsManaged: false,
                            Position: 1),
                        new RoleMenuRoleSnapshot(
                            RemovedRoleId,
                            "Removed",
                            IsEveryone: false,
                            IsManaged: false,
                            Position: 1)
                    ],
                    new RoleMenuActorSnapshot(
                        CanManageRoles: true,
                        Hierarchy: 10,
                        IsGuildOwner: false))),
            (_, _, _) => Task.FromResult<RoleMenuMemberSnapshot?>(
                new RoleMenuMemberSnapshot(GuildId, MemberUserId, [])),
            (_, _, _, _) =>
            {
                mutationCount++;
                return Task.CompletedTask;
            },
            (_, _, _, _) =>
            {
                mutationCount++;
                return Task.CompletedTask;
            });

        var result = await RoleMenuMemberWorkflow.ExecuteAsync(
            MenuId,
            GuildId,
            ChannelId,
            BotUserId,
            MemberUserId,
            MessageId,
            [RemovedRoleId.ToString(CultureInfo.InvariantCulture)],
            operations,
            CancellationToken.None);

        Assert.Equal(RoleMenuMemberWorkflowStatus.InvalidSelection, result.Status);
        Assert.Equal(RoleMenuSelectionIssue.RoleNotAllowed, result.SelectionIssue);
        Assert.Equal(0, mutationCount);
    }
}
