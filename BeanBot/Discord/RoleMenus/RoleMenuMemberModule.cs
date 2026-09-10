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

public sealed class RoleMenuMemberModule : RoleMenuModuleBase
{
    private const string InvalidMenuMessage =
        "This role menu is invalid, stale, or no longer available. Ask a server administrator to recreate it.";

    private readonly RoleMenuMemberService _members;
    private readonly ILogger<RoleMenuMemberModule> _logger;

    public RoleMenuMemberModule(
        RoleMenuInteractionService roleMenuService,
        RoleMenuMemberService members,
        ILogger<RoleMenuMemberModule> logger)
        : base(roleMenuService)
    {
        _members = members ?? throw new ArgumentNullException(nameof(members));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [ComponentInteraction(
        RoleMenuCustomIds.ManagePattern,
        ignoreGroupNames: true,
        runMode: RunMode.Sync)]
    public async Task ManageAsync(string menuIdValue)
    {
        using var cancellation = RoleMenus.CreateOperationCancellation();
        if (Context.Interaction is not SocketMessageComponent component)
        {
            await RoleMenus.ExecuteInitialResponseAsync(
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

        await RoleMenus.ExecuteInitialResponseAsync(
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
            var selector = await _members.LoadSelectorAsync(
                menuId,
                Context.Guild.Id,
                component.Message.Channel.Id,
                component.Message.Id,
                component.Message.Author.Id,
                Context.Guild.CurrentUser.Id,
                Context.User.Id,
                RoleMenuComponents.HasManageButton(component.Message, menuId),
                cancellation.Token);
            await ReplaceResponseAsync(selector.Content, cancellation.Token, components: selector.Components);
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
        using var cancellation = RoleMenus.CreateOperationCancellation();
        if (Context.Guild is null
            || Context.Interaction is not SocketMessageComponent component)
        {
            await RespondToInvalidComponentAsync(
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
            await RespondToInvalidComponentAsync(
                "That private role-menu control is invalid, expired, or belongs to another member.",
                cancellation.Token);
            return;
        }

        await RoleMenus.ExecuteInitialResponseAsync(
            supportsOriginalResponse: true,
            operationToken => component.UpdateAsync(
                properties => SetMessage(
                    properties,
                    "Applying your role choices…",
                    null,
                    MessageComponent.Empty),
                CreateRequestOptions(operationToken)),
            operationToken => ReplaceResponseAsync(
                "Applying your role choices…",
                operationToken),
            cancellation.Token);

        try
        {
            var content = await _members.ApplySelectionAsync(
                binding.MenuId,
                binding.PanelMessageId,
                selectedRoleValues,
                Context.Guild.Id,
                Context.Channel.Id,
                Context.Guild.CurrentUser.Id,
                Context.User.Id,
                cancellation.Token);
            await SendFreshFeedbackAsync(content);
        }
        catch (OperationCanceledException)
            when (cancellation.IsCancellationRequested && !RoleMenus.IsShuttingDown)
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

}
