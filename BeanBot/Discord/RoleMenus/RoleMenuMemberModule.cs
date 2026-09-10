using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using BeanBot.Logging;
using BeanBot.Persistence.Models;
using Discord;
using Discord.Interactions;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;

using static BeanBot.Discord.RoleMenus.DiscordRoleMenuClient;
using static BeanBot.Discord.RoleMenus.RoleMenuPresentation;
using static BeanBot.Discord.RoleMenus.RoleMenuSetupValidation;

namespace BeanBot.Discord.RoleMenus;

public sealed class RoleMenuMemberModule : InteractionModuleBase<SocketInteractionContext>
{
    private const string InvalidMenuMessage =
        "This role menu is invalid, stale, or no longer available. Ask a server administrator to recreate it.";

    private sealed record RoleMenuApplicationResult(string Content);

    private readonly RoleMenuInteractionService _roleMenuService;
    private readonly DiscordRoleMenuClient _discord;
    private readonly ILogger<RoleMenuMemberModule> _logger;

    public RoleMenuMemberModule(
        RoleMenuInteractionService roleMenuService,
        DiscordRoleMenuClient discord,
        ILogger<RoleMenuMemberModule> logger)
    {
        _roleMenuService = roleMenuService ?? throw new ArgumentNullException(nameof(roleMenuService));
        _discord = discord ?? throw new ArgumentNullException(nameof(discord));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [ComponentInteraction(
        RoleMenuCustomIds.ManagePattern,
        ignoreGroupNames: true,
        runMode: RunMode.Sync)]
    public async Task ManageAsync(string menuIdValue)
    {
        using var cancellation = _roleMenuService.CreateOperationCancellation();
        var requestOptions = CreateRequestOptions(cancellation.Token);
        if (Context.Interaction is not SocketMessageComponent component)
        {
            await _roleMenuService.ExecuteInitialResponseAsync(
                supportsOriginalResponse: true,
                operationToken => RespondAsync(
                    InvalidMenuMessage,
                    ephemeral: true,
                    allowedMentions: AllowedMentions.None,
                    options: CreateRequestOptions(operationToken)),
                operationToken => ReplaceResponseAsync(
                    InvalidMenuMessage,
                    operationToken),
                cancellation.Token);
            return;
        }

        await _roleMenuService.ExecuteInitialResponseAsync(
            supportsOriginalResponse: true,
            operationToken => component.DeferLoadingAsync(
                ephemeral: true,
                CreateRequestOptions(operationToken)),
            operationToken => ReplaceResponseAsync(
                "Loading your role menu…",
                operationToken),
            cancellation.Token);
        if (!RoleMenuCustomIds.TryParseMenuId(menuIdValue, out var menuId)
            || Context.Guild is null
            || IsEphemeral(component)
            || component.Data.Type != ComponentType.Button
            || !string.Equals(
                component.Data.CustomId,
                RoleMenuCustomIds.Manage(menuId),
                StringComparison.Ordinal))
        {
            await ReplaceResponseAsync(InvalidMenuMessage, cancellation.Token);
            return;
        }

        try
        {
            var settings = await _roleMenuService.GetAsync(
                menuId,
                Context.Guild.Id,
                cancellation.Token);
            if (!TryValidateSettings(settings, Context.Guild, out var parsed))
            {
                await ReplaceResponseAsync(InvalidMenuMessage, cancellation.Token);
                return;
            }

            var panelIssue = RoleMenuPanelContextValidator.Validate(
                parsed,
                Context.Guild.Id,
                component.Message.Channel.Id,
                component.Message.Id,
                component.Message.Author.Id,
                Context.Guild.CurrentUser.Id,
                RoleMenuComponents.HasManageButton(component.Message, menuId));
            if (panelIssue != RoleMenuPanelContextIssue.None)
            {
                LogInvalidConfiguration(menuId, panelIssue.ToString());
                await ReplaceResponseAsync(InvalidMenuMessage, cancellation.Token);
                return;
            }

            var currentBot = await _discord.GetGuildUserAsync(
                Context.Guild.Id,
                Context.Guild.CurrentUser.Id,
                requestOptions);
            if (currentBot is null)
            {
                LogInvalidConfiguration(menuId, "bot guild membership was not available");
                await ReplaceResponseAsync(InvalidMenuMessage, cancellation.Token);
                return;
            }

            var roleValidation = ValidateRoles(parsed.RoleIds, currentBot);
            if (!roleValidation.IsValid)
            {
                LogInvalidConfiguration(
                    menuId,
                    roleValidation.Issues[0].Kind.ToString());
                await ReplaceResponseAsync(InvalidMenuMessage, cancellation.Token);
                return;
            }

            var member = await _discord.GetGuildUserAsync(
                Context.Guild.Id,
                Context.User.Id,
                requestOptions);
            if (member is null)
            {
                await ReplaceResponseAsync(
                    "You are no longer a member of this server.",
                    cancellation.Token);
                return;
            }

            var selector = RoleMenuComponents.BuildMemberSelector(
                settings,
                parsed,
                roleValidation.Roles,
                member.RoleIds,
                Context.User.Id);
            var content = selector.HadConflictingSingleSelection
                ? "This single-choice menu found more than one configured role on your account. " +
                  "Choose the one to keep, or clear all menu roles. Changes apply immediately."
                : "Choose your roles below. Changes apply immediately; the clear button removes only " +
                  "roles configured in this menu.";
            await ReplaceResponseAsync(
                content,
                cancellation.Token,
                selector.Components);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            BeanBotLog.RoleMenuSelectionFailed(_logger, menuId.ToString(), exception);
            await ReplaceResponseAsync(
                "Bean Bot couldn't load this role menu. Try again in a moment.",
                cancellation.Token);
        }
    }

    [ComponentInteraction(
        RoleMenuCustomIds.SavePattern,
        ignoreGroupNames: true,
        runMode: RunMode.Sync)]
    public Task SaveAsync(
        string menuIdValue,
        string userIdValue,
        string panelMessageIdValue,
        string[] selectedRoleValues)
        => ApplySelectionAsync(
            menuIdValue,
            userIdValue,
            panelMessageIdValue,
            selectedRoleValues ?? [],
            ComponentType.SelectMenu);

    [ComponentInteraction(
        RoleMenuCustomIds.ClearPattern,
        ignoreGroupNames: true,
        runMode: RunMode.Sync)]
    public Task ClearAsync(
        string menuIdValue,
        string userIdValue,
        string panelMessageIdValue)
        => ApplySelectionAsync(
            menuIdValue,
            userIdValue,
            panelMessageIdValue,
            [],
            ComponentType.Button);

    private async Task ApplySelectionAsync(
        string menuIdValue,
        string userIdValue,
        string panelMessageIdValue,
        IReadOnlyCollection<string> selectedRoleValues,
        ComponentType expectedComponentType)
    {
        using var cancellation = _roleMenuService.CreateOperationCancellation();
        if (Context.Guild is null
            || Context.Interaction is not SocketMessageComponent component)
        {
            await RespondToInvalidPrivateComponentAsync(
                "That private role-menu control is invalid, expired, or belongs to another member.",
                cancellation.Token);
            return;
        }

        var controlIssue = RoleMenuPrivateControlValidator.Validate(
            menuIdValue,
            userIdValue,
            panelMessageIdValue,
            Context.User.Id,
            IsEphemeral(component),
            component.Data.Type,
            expectedComponentType,
            component.Message.Author.Id,
            Context.Guild.CurrentUser.Id,
            HasComponent(
                component.Message,
                component.Data.CustomId,
                expectedComponentType),
            out var binding);
        if (controlIssue != RoleMenuPrivateControlIssue.None)
        {
            await RespondToInvalidPrivateComponentAsync(
                "That private role-menu control is invalid, expired, or belongs to another member.",
                cancellation.Token);
            return;
        }

        await _roleMenuService.ExecuteInitialResponseAsync(
            supportsOriginalResponse: true,
            operationToken => component.UpdateAsync(
                properties => SetMessage(
                    properties,
                    "Applying your role choices…",
                    MessageComponent.Empty),
                CreateRequestOptions(operationToken)),
            operationToken => ReplaceResponseAsync(
                "Applying your role choices…",
                operationToken),
            cancellation.Token);

        try
        {
            var result = await _roleMenuService.RunMemberMutationAsync(
                binding.MenuId,
                Context.Guild.Id,
                Context.User.Id,
                operationToken => ApplySelectionCoreAsync(
                    binding.MenuId,
                    binding.PanelMessageId,
                    selectedRoleValues,
                    Context.Guild,
                    operationToken),
                cancellation.Token);
            await SendApplicationResultAsync(result);
        }
        catch (OperationCanceledException)
            when (cancellation.IsCancellationRequested && !_roleMenuService.IsShuttingDown)
        {
            await SendFreshFeedbackAsync(
                "Bean Bot ran out of time before it could confirm the final result. Open the role " +
                "menu again to check your current roles before retrying.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            BeanBotLog.RoleMenuSelectionFailed(
                _logger,
                binding.MenuId.ToString(),
                exception);
            await SendFreshFeedbackAsync(
                "Bean Bot couldn't confirm the final result. Open the role menu again to check " +
                "your current roles before retrying.");
        }
    }

    private Task SendApplicationResultAsync(RoleMenuApplicationResult result)
        => SendFreshFeedbackAsync(result.Content);

    private async Task SendFreshFeedbackAsync(string content)
    {
        if (_roleMenuService.IsShuttingDown)
        {
            throw new OperationCanceledException();
        }

        using var feedbackCancellation = _roleMenuService.CreateFeedbackCancellation();
        await ReplaceResponseAsync(content, feedbackCancellation.Token);
    }

    private async Task<RoleMenuApplicationResult> ApplySelectionCoreAsync(
        ObjectId menuId,
        ulong boundPanelMessageId,
        IReadOnlyCollection<string> selectedRoleValues,
        SocketGuild guild,
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
                    guild.Id,
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
            guild.Id,
            Context.Channel.Id,
            guild.CurrentUser.Id,
            Context.User.Id,
            boundPanelMessageId,
            selectedRoleValues,
            operations,
            cancellationToken);
        return FormatWorkflowResult(menuId, result);
    }

    private RoleMenuApplicationResult FormatWorkflowResult(
        ObjectId menuId,
        RoleMenuMemberWorkflowResult result)
    {
        if (result.Status == RoleMenuMemberWorkflowStatus.InvalidConfiguration)
        {
            LogInvalidConfiguration(menuId, FormatConfigurationIssue(result));
            return new RoleMenuApplicationResult(InvalidMenuMessage);
        }

        if (result.Status == RoleMenuMemberWorkflowStatus.MemberUnavailable)
        {
            return new RoleMenuApplicationResult(
                "You are no longer a member of this server.");
        }

        if (result.Status == RoleMenuMemberWorkflowStatus.InvalidSelection)
        {
            LogInvalidConfiguration(
                menuId,
                $"invalid submitted selection: {result.SelectionIssue}");
            return new RoleMenuApplicationResult(
                "That role selection was invalid or had been tampered with. No roles were changed.");
        }

        var roleNames = (result.Roles ?? [])
            .ToDictionary(role => role.Id, role => role.Name);
        LogSynchronizationResult(menuId, result.Synchronization, roleNames);
        if (result.Reconciliation is not null)
        {
            return new RoleMenuApplicationResult(
                FormatReconciliation(result.Reconciliation, roleNames));
        }

        if (result.FinalReadException is not null)
        {
            BeanBotLog.RoleMenuReconciliationFailed(
                _logger,
                menuId.ToString(),
                result.FinalReadException);
        }

        return new RoleMenuApplicationResult(
            "Bean Bot couldn't recheck Discord's final role state. Open the role menu again " +
            "to verify your current roles before retrying; no roles outside this menu were targeted.");
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
        SocketGuild guild,
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

        if (parsed.GuildId != guild.Id)
        {
            LogInvalidConfiguration(settings.Id, RoleMenuPanelContextIssue.GuildMismatch.ToString());
            parsed = null;
            return false;
        }

        return true;
    }

    private async Task RespondToInvalidPrivateComponentAsync(
        string message,
        CancellationToken cancellationToken)
    {
        if (Context.Interaction is SocketMessageComponent component && IsEphemeral(component))
        {
            await _roleMenuService.ExecuteInitialResponseAsync(
                supportsOriginalResponse: true,
                operationToken => component.UpdateAsync(
                    properties => SetMessage(
                        properties,
                        message,
                        MessageComponent.Empty),
                    CreateRequestOptions(operationToken)),
                operationToken => ReplaceResponseAsync(message, operationToken),
                cancellationToken);
            return;
        }

        if (Context.Interaction is SocketMessageComponent publicComponent)
        {
            await _roleMenuService.ExecuteInitialResponseAsync(
                supportsOriginalResponse: true,
                operationToken => publicComponent.DeferLoadingAsync(
                    ephemeral: true,
                    CreateRequestOptions(operationToken)),
                operationToken => ReplaceResponseAsync(message, operationToken),
                cancellationToken);
            await ReplaceResponseAsync(message, cancellationToken);
            return;
        }

        await _roleMenuService.ExecuteInitialResponseAsync(
            supportsOriginalResponse: true,
            operationToken => RespondAsync(
                message,
                ephemeral: true,
                allowedMentions: AllowedMentions.None,
                options: CreateRequestOptions(operationToken)),
            operationToken => ReplaceResponseAsync(message, operationToken),
            cancellationToken);
    }

    private Task<IUserMessage> ReplaceResponseAsync(
        string content,
        CancellationToken cancellationToken,
        MessageComponent? components = null)
        => ModifyOriginalResponseAsync(
            properties => SetMessage(
                properties,
                content,
                components ?? MessageComponent.Empty),
            CreateRequestOptions(cancellationToken));

    private static void SetMessage(
        MessageProperties properties,
        string content,
        MessageComponent components)
    {
        properties.Content = content;
        properties.Embeds = Array.Empty<Embed>();
        properties.Components = components;
        properties.AllowedMentions = AllowedMentions.None;
    }

    private void LogInvalidConfiguration(ObjectId menuId, string reason)
        => BeanBotLog.RoleMenuConfigurationInvalid(
            _logger,
            menuId.ToString(),
            reason);

    private static bool HasComponent(
        IMessage message,
        string customId,
        ComponentType componentType)
        => message.Components
            .OfType<ActionRowComponent>()
            .SelectMany(row => row.Components)
            .OfType<IInteractableComponent>()
            .Any(component => component.Type == componentType
                              && string.Equals(
                                  component.CustomId,
                                  customId,
                                  StringComparison.Ordinal));

    private static bool IsEphemeral(SocketMessageComponent component)
        => component.Message.Flags?.HasFlag(MessageFlags.Ephemeral) == true;
}
