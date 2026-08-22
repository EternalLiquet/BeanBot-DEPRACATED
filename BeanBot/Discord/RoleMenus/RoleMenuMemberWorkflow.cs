using BeanBot.Persistence.Models;
using MongoDB.Bson;

namespace BeanBot.Discord.RoleMenus;

internal sealed record RoleMenuBotSnapshot(
    ulong GuildId,
    ulong UserId,
    IReadOnlyList<RoleMenuRoleSnapshot> Roles,
    RoleMenuActorSnapshot Actor);

internal sealed record RoleMenuMemberSnapshot(
    ulong GuildId,
    ulong UserId,
    IReadOnlyList<ulong> RoleIds);

internal sealed record RoleMenuMemberOperations(
    Func<ObjectId, ulong, CancellationToken, Task<RoleMenuSettings?>> ReadSettings,
    Func<ObjectId, ulong, ulong, CancellationToken, Task<RoleMenuPanelSnapshot?>> ReadPanel,
    Func<ulong, ulong, CancellationToken, Task<RoleMenuBotSnapshot?>> ReadBot,
    Func<ulong, ulong, CancellationToken, Task<RoleMenuMemberSnapshot?>> ReadMember,
    Func<ulong, ulong, ulong, CancellationToken, Task> AddRole,
    Func<ulong, ulong, ulong, CancellationToken, Task> RemoveRole);

internal enum RoleMenuMemberWorkflowStatus
{
    InvalidConfiguration,
    MemberUnavailable,
    InvalidSelection,
    ConfirmedComplete,
    ConfirmedIncomplete,
    Unconfirmed
}

internal enum RoleMenuMemberConfigurationIssue
{
    None,
    SettingsMissing,
    SettingsIdentityMismatch,
    SettingsInvalid,
    GuildMismatch,
    BoundPanelMismatch,
    PanelMissing,
    PanelInvalid,
    BotMissing,
    BotSnapshotMismatch,
    RolesInvalid,
    MemberSnapshotMismatch
}

internal enum RoleMenuMemberFinalStateIssue
{
    None,
    MemberMissing,
    SnapshotMismatch,
    ReadFailed
}

internal sealed record RoleMenuMemberWorkflowResult(
    RoleMenuMemberWorkflowStatus Status,
    RoleMenuMemberConfigurationIssue ConfigurationIssue =
        RoleMenuMemberConfigurationIssue.None,
    RoleMenuSettingsIssue SettingsIssue = RoleMenuSettingsIssue.None,
    RoleMenuPanelContextIssue PanelIssue = RoleMenuPanelContextIssue.None,
    IReadOnlyList<RoleMenuRoleSnapshot>? Roles = null,
    IReadOnlyList<RoleMenuRoleIssue>? RoleIssues = null,
    RoleMenuSelectionIssue SelectionIssue = RoleMenuSelectionIssue.None,
    RoleMenuSynchronizationResult? Synchronization = null,
    RoleMenuSelectionReconciliation? Reconciliation = null,
    RoleMenuMemberFinalStateIssue FinalStateIssue = RoleMenuMemberFinalStateIssue.None,
    Exception? FinalReadException = null)
{
    internal bool IsConfirmed
        => Status is RoleMenuMemberWorkflowStatus.ConfirmedComplete
            or RoleMenuMemberWorkflowStatus.ConfirmedIncomplete;

    internal bool IsComplete
        => Status == RoleMenuMemberWorkflowStatus.ConfirmedComplete;
}

internal static class RoleMenuMemberWorkflow
{
    private sealed class DelegateMemberMutator(
        ulong guildId,
        ulong userId,
        RoleMenuMemberOperations operations) : IRoleMenuMemberMutator
    {
        public Task AddRoleAsync(ulong roleId, CancellationToken cancellationToken)
            => operations.AddRole(guildId, userId, roleId, cancellationToken);

        public Task RemoveRoleAsync(ulong roleId, CancellationToken cancellationToken)
            => operations.RemoveRole(guildId, userId, roleId, cancellationToken);
    }

    internal static async Task<RoleMenuMemberWorkflowResult> ExecuteAsync(
        ObjectId menuId,
        ulong guildId,
        ulong interactionChannelId,
        ulong botUserId,
        ulong memberUserId,
        ulong boundPanelMessageId,
        IReadOnlyCollection<string> selectedRoleValues,
        RoleMenuMemberOperations operations,
        CancellationToken cancellationToken)
    {
        if (menuId == ObjectId.Empty)
        {
            throw new ArgumentException("A role menu ID is required.", nameof(menuId));
        }

        ArgumentOutOfRangeException.ThrowIfZero(guildId);
        ArgumentOutOfRangeException.ThrowIfZero(botUserId);
        ArgumentOutOfRangeException.ThrowIfZero(interactionChannelId);
        ArgumentOutOfRangeException.ThrowIfZero(memberUserId);
        ArgumentOutOfRangeException.ThrowIfZero(boundPanelMessageId);
        ArgumentNullException.ThrowIfNull(selectedRoleValues);
        ArgumentNullException.ThrowIfNull(operations);
        ValidateOperations(operations);

        var settings = await operations.ReadSettings(menuId, guildId, cancellationToken);
        if (settings is null)
        {
            return InvalidConfiguration(RoleMenuMemberConfigurationIssue.SettingsMissing);
        }

        if (settings.Id != menuId)
        {
            return InvalidConfiguration(
                RoleMenuMemberConfigurationIssue.SettingsIdentityMismatch);
        }

        if (!RoleMenuSettingsParser.TryParse(settings, out var parsed, out var settingsIssue))
        {
            return InvalidConfiguration(
                RoleMenuMemberConfigurationIssue.SettingsInvalid,
                settingsIssue);
        }

        if (parsed.GuildId != guildId)
        {
            return InvalidConfiguration(RoleMenuMemberConfigurationIssue.GuildMismatch);
        }

        if (parsed.MessageId != boundPanelMessageId)
        {
            return InvalidConfiguration(RoleMenuMemberConfigurationIssue.BoundPanelMismatch);
        }

        var panel = await operations.ReadPanel(
            menuId,
            parsed.ChannelId,
            parsed.MessageId,
            cancellationToken);
        if (panel is null)
        {
            return InvalidConfiguration(RoleMenuMemberConfigurationIssue.PanelMissing);
        }

        var panelIssue = RoleMenuPanelContextValidator.Validate(
            parsed,
            guildId,
            interactionChannelId,
            panel.MessageId,
            panel.AuthorId,
            botUserId,
            panel.HasManageButton);
        if (panelIssue != RoleMenuPanelContextIssue.None
            || panel.GuildId != guildId
            || panel.ChannelId != parsed.ChannelId)
        {
            var normalizedPanelIssue = panel.GuildId != guildId
                ? RoleMenuPanelContextIssue.GuildMismatch
                : panel.ChannelId != parsed.ChannelId
                    ? RoleMenuPanelContextIssue.ChannelMismatch
                    : panelIssue;
            return InvalidConfiguration(
                RoleMenuMemberConfigurationIssue.PanelInvalid,
                panelIssue: normalizedPanelIssue);
        }

        var bot = await operations.ReadBot(guildId, botUserId, cancellationToken);
        if (bot is null)
        {
            return InvalidConfiguration(RoleMenuMemberConfigurationIssue.BotMissing);
        }

        if (bot.GuildId != guildId
            || bot.UserId != botUserId
            || bot.Roles is null)
        {
            return InvalidConfiguration(RoleMenuMemberConfigurationIssue.BotSnapshotMismatch);
        }

        var roleValidation = RoleMenuRoleValidator.Validate(
            parsed.RoleIds,
            bot.Roles,
            bot.Actor);
        if (!roleValidation.IsValid)
        {
            return new RoleMenuMemberWorkflowResult(
                RoleMenuMemberWorkflowStatus.InvalidConfiguration,
                RoleMenuMemberConfigurationIssue.RolesInvalid,
                Roles: roleValidation.Roles,
                RoleIssues: roleValidation.Issues);
        }

        var member = await operations.ReadMember(guildId, memberUserId, cancellationToken);
        if (member is null)
        {
            return new RoleMenuMemberWorkflowResult(
                RoleMenuMemberWorkflowStatus.MemberUnavailable,
                Roles: roleValidation.Roles);
        }

        if (!IsExpectedMember(member, guildId, memberUserId))
        {
            return new RoleMenuMemberWorkflowResult(
                RoleMenuMemberWorkflowStatus.InvalidConfiguration,
                RoleMenuMemberConfigurationIssue.MemberSnapshotMismatch,
                Roles: roleValidation.Roles);
        }

        var planResult = RoleMenuSelectionPlanner.Create(
            parsed.RoleIds,
            selectedRoleValues,
            member.RoleIds,
            settings.SelectionMode);
        if (!planResult.IsValid)
        {
            return new RoleMenuMemberWorkflowResult(
                RoleMenuMemberWorkflowStatus.InvalidSelection,
                Roles: roleValidation.Roles,
                SelectionIssue: planResult.Issue);
        }

        var beforeRoleIds = member.RoleIds.ToList();
        var synchronization = await RoleMenuMemberSynchronizer.SynchronizeAsync(
            planResult.Plan,
            settings.SelectionMode,
            new DelegateMemberMutator(guildId, memberUserId, operations),
            cancellationToken);
        return await ReadFinalStateAsync(
            guildId,
            memberUserId,
            parsed.RoleIds,
            planResult.Plan.SelectedRoleIds,
            beforeRoleIds,
            roleValidation.Roles,
            synchronization,
            operations);
    }

    private static async Task<RoleMenuMemberWorkflowResult> ReadFinalStateAsync(
        ulong guildId,
        ulong memberUserId,
        IReadOnlyCollection<ulong> configuredRoleIds,
        IReadOnlyCollection<ulong> selectedRoleIds,
        IReadOnlyCollection<ulong> beforeRoleIds,
        IReadOnlyList<RoleMenuRoleSnapshot> roles,
        RoleMenuSynchronizationResult synchronization,
        RoleMenuMemberOperations operations)
    {
        using var finalReadCancellation = new CancellationTokenSource(
            RoleMenuConstants.CleanupTimeout);
        RoleMenuMemberSnapshot? finalMember;
        try
        {
            finalMember = await operations.ReadMember(
                guildId,
                memberUserId,
                finalReadCancellation.Token);
        }
        catch (Exception exception)
        {
            return Unconfirmed(
                roles,
                synchronization,
                RoleMenuMemberFinalStateIssue.ReadFailed,
                exception);
        }

        if (finalMember is null)
        {
            return Unconfirmed(
                roles,
                synchronization,
                RoleMenuMemberFinalStateIssue.MemberMissing);
        }

        if (!IsExpectedMember(finalMember, guildId, memberUserId))
        {
            return Unconfirmed(
                roles,
                synchronization,
                RoleMenuMemberFinalStateIssue.SnapshotMismatch);
        }

        var reconciliation = RoleMenuSelectionReconciler.Create(
            configuredRoleIds,
            selectedRoleIds,
            beforeRoleIds,
            finalMember.RoleIds);
        return new RoleMenuMemberWorkflowResult(
            reconciliation.IsComplete
                ? RoleMenuMemberWorkflowStatus.ConfirmedComplete
                : RoleMenuMemberWorkflowStatus.ConfirmedIncomplete,
            Roles: roles,
            Synchronization: synchronization,
            Reconciliation: reconciliation);
    }

    private static bool IsExpectedMember(
        RoleMenuMemberSnapshot member,
        ulong guildId,
        ulong memberUserId)
        => member.GuildId == guildId
           && member.UserId == memberUserId
           && member.RoleIds is not null;

    private static RoleMenuMemberWorkflowResult InvalidConfiguration(
        RoleMenuMemberConfigurationIssue issue,
        RoleMenuSettingsIssue settingsIssue = RoleMenuSettingsIssue.None,
        RoleMenuPanelContextIssue panelIssue = RoleMenuPanelContextIssue.None)
        => new(
            RoleMenuMemberWorkflowStatus.InvalidConfiguration,
            issue,
            settingsIssue,
            panelIssue);

    private static RoleMenuMemberWorkflowResult Unconfirmed(
        IReadOnlyList<RoleMenuRoleSnapshot> roles,
        RoleMenuSynchronizationResult synchronization,
        RoleMenuMemberFinalStateIssue finalStateIssue,
        Exception? exception = null)
        => new(
            RoleMenuMemberWorkflowStatus.Unconfirmed,
            Roles: roles,
            Synchronization: synchronization,
            FinalStateIssue: finalStateIssue,
            FinalReadException: exception);

    private static void ValidateOperations(RoleMenuMemberOperations operations)
    {
        ArgumentNullException.ThrowIfNull(operations.ReadSettings);
        ArgumentNullException.ThrowIfNull(operations.ReadPanel);
        ArgumentNullException.ThrowIfNull(operations.ReadBot);
        ArgumentNullException.ThrowIfNull(operations.ReadMember);
        ArgumentNullException.ThrowIfNull(operations.AddRole);
        ArgumentNullException.ThrowIfNull(operations.RemoveRole);
    }
}
