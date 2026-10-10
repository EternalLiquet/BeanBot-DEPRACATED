using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using BeanBot.Logging;
using BeanBot.Persistence.Models;
using Discord;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using static BeanBot.Discord.RoleMenus.DiscordRoleMenuClient;
using static BeanBot.Discord.RoleMenus.RoleMenuPresentation;
using static BeanBot.Discord.RoleMenus.RoleMenuSetupValidation;

namespace BeanBot.Discord.RoleMenus;

public sealed class RoleMenuMemberService
{
    private const string InvalidMenuMessage =
        "This role menu isn't working anymore. Ask a server admin to set it up again.";
    private readonly RoleMenuInteractionService _roleMenuService;
    private readonly DiscordRoleMenuClient _discord;
    private readonly ILogger<RoleMenuMemberService> _logger;

    public RoleMenuMemberService(
        RoleMenuInteractionService roleMenuService,
        DiscordRoleMenuClient discord,
        ILogger<RoleMenuMemberService> logger)
    {
        _roleMenuService = roleMenuService ?? throw new ArgumentNullException(nameof(roleMenuService));
        _discord = discord ?? throw new ArgumentNullException(nameof(discord));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    internal Task<string> ApplySelectionAsync(
        ObjectId menuId,
        ulong boundPanelMessageId,
        IReadOnlyCollection<string> selectedRoleValues,
        ulong guildId,
        ulong channelId,
        ulong botUserId,
        ulong memberUserId,
        CancellationToken cancellationToken)
        => _roleMenuService.RunMemberMutationAsync(
            menuId,
            guildId,
            memberUserId,
            operationToken => ApplySelectionCoreAsync(
                menuId, boundPanelMessageId, selectedRoleValues,
                guildId, channelId, botUserId, memberUserId, operationToken),
            cancellationToken);

    internal async Task<RoleMenuSelectorResult> LoadSelectorAsync(
        ObjectId menuId,
        ulong guildId,
        ulong channelId,
        ulong messageId,
        ulong authorId,
        ulong botUserId,
        ulong memberUserId,
        bool hasManageButton,
        CancellationToken cancellationToken)
    {
        var requestOptions = CreateRequestOptions(cancellationToken);
        var settings = await _roleMenuService.GetAsync(
            menuId,
            guildId,
            cancellationToken);
        if (!TryValidateSettings(settings, guildId, out var parsed))
        {
            return new RoleMenuSelectorResult(InvalidMenuMessage);
        }

        var panelIssue = RoleMenuPanelContextValidator.Validate(
            parsed,
            guildId,
            channelId,
            messageId,
            authorId,
            botUserId,
            hasManageButton);
        if (panelIssue != RoleMenuPanelContextIssue.None)
        {
            LogInvalidConfiguration(menuId, panelIssue.ToString());
            return new RoleMenuSelectorResult(InvalidMenuMessage);
        }

        var currentBot = await _discord.GetGuildUserAsync(
            guildId,
            botUserId,
            requestOptions);
        if (currentBot is null)
        {
            LogInvalidConfiguration(menuId, "bot guild membership was not available");
            return new RoleMenuSelectorResult(InvalidMenuMessage);
        }

        var roleValidation = ValidateRoles(parsed.RoleIds, currentBot);
        if (!roleValidation.IsValid)
        {
            LogInvalidConfiguration(
                menuId,
                roleValidation.Issues[0].Kind.ToString());
            return new RoleMenuSelectorResult(InvalidMenuMessage);
        }

        var member = await _discord.GetGuildUserAsync(
            guildId,
            memberUserId,
            requestOptions);
        if (member is null)
        {
            return new RoleMenuSelectorResult("You're no longer in this server.");
        }

        var selector = RoleMenuComponents.BuildMemberSelector(
            settings,
            parsed,
            roleValidation.Roles,
            member.RoleIds,
            memberUserId);
        var content = FormatSelectorInstructions(
            settings.SelectionMode,
            selector.HadConflictingSingleSelection);
        return new RoleMenuSelectorResult(content, selector.Components);
    }

    private async Task<string> ApplySelectionCoreAsync(
        ObjectId menuId,
        ulong boundPanelMessageId,
        IReadOnlyCollection<string> selectedRoleValues,
        ulong guildId,
        ulong channelId,
        ulong botUserId,
        ulong memberUserId,
        CancellationToken cancellationToken)
    {
        IGuildUser? mutationMember = null;
        var operations = new RoleMenuMemberOperations(
            (requestedMenuId, requestedGuildId, operationToken) =>
                _roleMenuService.GetAsync(
                    requestedMenuId,
                    requestedGuildId,
                    operationToken),
            (requestedMenuId, channelId, messageId, operationToken) =>
                _discord.ReadPanelSnapshotAsync(
                    guildId,
                    requestedMenuId,
                    channelId,
                    messageId,
                    operationToken),
            (requestedGuildId, requestedBotUserId, operationToken) =>
                _discord.ReadBotSnapshotAsync(
                    requestedGuildId,
                    requestedBotUserId,
                    operationToken),
            async (requestedGuildId, requestedMemberUserId, operationToken) =>
            {
                var member = await _discord.GetGuildUserAsync(
                    requestedGuildId,
                    requestedMemberUserId,
                    CreateRequestOptions(operationToken));
                mutationMember ??= member;
                return member is null ? null : CreateMemberSnapshot(member);
            },
            (requestedGuildId, requestedMemberUserId, roleId, operationToken) =>
                AddMemberRoleAsync(
                    mutationMember,
                    requestedGuildId,
                    requestedMemberUserId,
                    roleId,
                    operationToken),
            (requestedGuildId, requestedMemberUserId, roleId, operationToken) =>
                RemoveMemberRoleAsync(
                    mutationMember,
                    requestedGuildId,
                    requestedMemberUserId,
                    roleId,
                    operationToken));
        var result = await RoleMenuMemberWorkflow.ExecuteAsync(
            menuId,
            guildId,
            channelId,
            botUserId,
            memberUserId,
            boundPanelMessageId,
            selectedRoleValues,
            operations,
            cancellationToken);
        return FormatWorkflowResult(menuId, result);
    }

    private string FormatWorkflowResult(
        ObjectId menuId,
        RoleMenuMemberWorkflowResult result)
    {
        if (result.Status == RoleMenuMemberWorkflowStatus.InvalidConfiguration)
        {
            LogInvalidConfiguration(menuId, FormatConfigurationIssue(result));
            return InvalidMenuMessage;
        }

        if (result.Status == RoleMenuMemberWorkflowStatus.MemberUnavailable)
        {
            return "You're no longer in this server.";
        }

        if (result.Status == RoleMenuMemberWorkflowStatus.InvalidSelection)
        {
            LogInvalidConfiguration(
                menuId,
                $"invalid submitted selection: {result.SelectionIssue}");
            return "That selection is no longer valid. Open the menu again and choose your roles. " +
                "No roles were changed.";
        }

        var roleNames = (result.Roles ?? [])
            .ToDictionary(role => role.Id, role => role.Name);
        LogSynchronizationResult(menuId, result.Synchronization, roleNames);
        if (result.Reconciliation is not null)
        {
            return FormatReconciliation(result.Reconciliation, roleNames);
        }

        if (result.FinalReadException is not null)
        {
            BeanBotLog.RoleMenuReconciliationFailed(
                _logger,
                menuId.ToString(),
                result.FinalReadException);
        }

        return "I couldn't confirm your role changes. Open the menu again to check your roles " +
            "before trying again.";
    }

    internal static string FormatSelectorInstructions(
        RoleMenuSelectionMode selectionMode,
        bool hadConflictingSingleSelection)
    {
        if (hadConflictingSingleSelection)
        {
            return "You have more than one role from this menu. Choose one to keep, or remove " +
                "them all. Your changes apply right away.";
        }

        return selectionMode == RoleMenuSelectionMode.Exclusive
            ? "Choose one role. Picking a different one replaces the one you have. Your changes " +
              "apply right away."
            : "Choose the roles you want. Your changes apply right away.";
    }

    private void LogSynchronizationResult(
        ObjectId menuId,
        RoleMenuSynchronizationResult? result,
        IReadOnlyDictionary<ulong, string> roleNames)
    {
        if (result is null)
        {
            return;
        }

        foreach (var failure in result.Failures)
        {
            BeanBotLog.RoleMenuMutationFailed(
                _logger,
                menuId.ToString(),
                failure.Action,
                failure.RoleId.ToString(CultureInfo.InvariantCulture),
                GetRoleName(roleNames, failure.RoleId),
                failure.Exception);
        }

        if (result.Interruption is not null)
        {
            BeanBotLog.RoleMenuMutationInterrupted(
                _logger,
                menuId.ToString(),
                result.Interruption.Action,
                result.Interruption.RoleId.ToString(CultureInfo.InvariantCulture),
                GetRoleName(roleNames, result.Interruption.RoleId),
                result.Interruption.Kind.ToString());
        }

        BeanBotLog.RoleMenuSelectionCompleted(
            _logger,
            menuId,
            result.AddedRoleIds.Count,
            result.RemovedRoleIds.Count,
            result.Failures.Count);
    }

    private bool TryValidateSettings(
        [NotNullWhen(true)] RoleMenuSettings? settings,
        ulong guildId,
        [NotNullWhen(true)] out ParsedRoleMenuSettings? parsed)
    {
        if (settings is null)
        {
            parsed = null;
            return false;
        }

        if (!RoleMenuSettingsParser.TryParse(settings, out parsed, out var issue))
        {
            LogInvalidConfiguration(settings.Id, issue.ToString());
            parsed = null;
            return false;
        }

        if (parsed.GuildId != guildId)
        {
            LogInvalidConfiguration(settings.Id, RoleMenuPanelContextIssue.GuildMismatch.ToString());
            parsed = null;
            return false;
        }

        return true;
    }

    private void LogInvalidConfiguration(ObjectId menuId, string reason)
        => BeanBotLog.RoleMenuConfigurationInvalid(
            _logger,
            menuId.ToString(),
            reason);

}

internal sealed record RoleMenuSelectorResult(string Content, MessageComponent? Components = null);
