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

namespace BeanBot.Discord.RoleMenus;

public sealed class RoleMenuMemberModule : InteractionModuleBase<SocketInteractionContext>
{
    private const string InvalidMenuMessage =
        "This role menu is invalid, stale, or no longer available. Ask a server administrator to recreate it.";

    private sealed record RoleMenuApplicationResult(string Content);

    private readonly RoleMenuInteractionService _roleMenuService;
    private readonly ILogger<RoleMenuMemberModule> _logger;

    public RoleMenuMemberModule(
        RoleMenuInteractionService roleMenuService,
        ILogger<RoleMenuMemberModule> logger)
    {
        _roleMenuService = roleMenuService ?? throw new ArgumentNullException(nameof(roleMenuService));
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
            await RespondAsync(
                InvalidMenuMessage,
                ephemeral: true,
                allowedMentions: AllowedMentions.None,
                options: requestOptions);
            return;
        }

        await component.DeferLoadingAsync(ephemeral: true, requestOptions);
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

            var currentBot = await GetGuildMemberAsync(
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

            var member = await GetGuildMemberAsync(
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
        var requestOptions = CreateRequestOptions(cancellation.Token);
        if (Context.Guild is null
            || Context.Interaction is not SocketMessageComponent component)
        {
            await RespondToInvalidPrivateComponentAsync(
                "That private role-menu control is invalid, expired, or belongs to another member.",
                requestOptions,
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
                requestOptions,
                cancellation.Token);
            return;
        }

        await component.UpdateAsync(
            properties => SetMessage(
                properties,
                "Applying your role choices…",
                MessageComponent.Empty),
            requestOptions);

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
                ReadPanelSnapshotAsync(
                    guild,
                    requestedMenuId,
                    channelId,
                    messageId,
                    operationToken),
            (requestedGuildId, requestedBotUserId, operationToken) =>
                ReadBotSnapshotAsync(
                    requestedGuildId,
                    requestedBotUserId,
                    operationToken),
            async (requestedGuildId, requestedMemberUserId, operationToken) =>
            {
                var member = await GetGuildMemberAsync(
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

    private static string FormatConfigurationIssue(RoleMenuMemberWorkflowResult result)
        => result.ConfigurationIssue switch
        {
            RoleMenuMemberConfigurationIssue.SettingsInvalid =>
                $"{result.ConfigurationIssue}: {result.SettingsIssue}",
            RoleMenuMemberConfigurationIssue.PanelInvalid =>
                $"{result.ConfigurationIssue}: {result.PanelIssue}",
            RoleMenuMemberConfigurationIssue.RolesInvalid when result.RoleIssues is { Count: > 0 } =>
                $"{result.ConfigurationIssue}: {result.RoleIssues[0].Kind}",
            _ => result.ConfigurationIssue.ToString()
        };

    private static async Task<RoleMenuPanelSnapshot?> ReadPanelSnapshotAsync(
        SocketGuild guild,
        ObjectId menuId,
        ulong channelId,
        ulong messageId,
        CancellationToken cancellationToken)
    {
        var requestOptions = CreateRequestOptions(cancellationToken);
        var channel = await ((IGuild)guild).GetTextChannelAsync(
            channelId,
            CacheMode.AllowDownload,
            requestOptions);
        if (channel is null)
        {
            return null;
        }

        try
        {
            var message = await channel.GetMessageAsync(
                messageId,
                CacheMode.AllowDownload,
                requestOptions);
            return message is null
                ? null
                : new RoleMenuPanelSnapshot(
                    channel.GuildId,
                    channel.Id,
                    message.Id,
                    message.Author.Id,
                    RoleMenuComponents.HasManageButton(message, menuId));
        }
        catch (HttpException exception) when (exception.HttpCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task<RoleMenuBotSnapshot?> ReadBotSnapshotAsync(
        ulong guildId,
        ulong botUserId,
        CancellationToken cancellationToken)
    {
        var bot = await GetGuildMemberAsync(
            guildId,
            botUserId,
            CreateRequestOptions(cancellationToken));
        if (bot is null)
        {
            return null;
        }

        return new RoleMenuBotSnapshot(
            bot.Guild.Id,
            bot.Id,
            CreateRoleSnapshots(bot),
            CreateActorSnapshot(bot));
    }

    private static RoleMenuMemberSnapshot CreateMemberSnapshot(IGuildUser member)
        => new(member.Guild.Id, member.Id, member.RoleIds.ToList());

    private static Task AddMemberRoleAsync(
        IGuildUser? member,
        ulong guildId,
        ulong memberUserId,
        ulong roleId,
        CancellationToken cancellationToken)
    {
        EnsureExpectedMember(member, guildId, memberUserId);
        return member.AddRoleAsync(roleId, CreateRequestOptions(cancellationToken));
    }

    private static Task RemoveMemberRoleAsync(
        IGuildUser? member,
        ulong guildId,
        ulong memberUserId,
        ulong roleId,
        CancellationToken cancellationToken)
    {
        EnsureExpectedMember(member, guildId, memberUserId);
        return member.RemoveRoleAsync(roleId, CreateRequestOptions(cancellationToken));
    }

    private static void EnsureExpectedMember(
        [NotNull] IGuildUser? member,
        ulong guildId,
        ulong memberUserId)
    {
        if (member is null
            || member.Guild.Id != guildId
            || member.Id != memberUserId)
        {
            throw new InvalidOperationException(
                "The role-menu member mutation was not bound to the expected guild member.");
        }
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

    private async Task<IGuildUser?> GetGuildMemberAsync(
        ulong guildId,
        ulong userId,
        RequestOptions requestOptions)
    {
        try
        {
            return await Context.Client.Rest.GetGuildUserAsync(
                guildId,
                userId,
                requestOptions);
        }
        catch (HttpException exception) when (exception.HttpCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static RoleMenuRoleValidationResult ValidateRoles(
        IReadOnlyCollection<ulong> roleIds,
        IGuildUser bot)
    {
        return RoleMenuRoleValidator.Validate(
            roleIds,
            CreateRoleSnapshots(bot),
            CreateActorSnapshot(bot));
    }

    private static List<RoleMenuRoleSnapshot> CreateRoleSnapshots(
        IGuildUser user)
        => [.. user.Guild.Roles
            .Select(role => new RoleMenuRoleSnapshot(
                role.Id,
                role.Name,
                role.Id == user.Guild.EveryoneRole.Id,
                role.IsManaged,
                role.Position))];

    private static RoleMenuActorSnapshot CreateActorSnapshot(IGuildUser user)
    {
        var hierarchy = user.Guild.Roles
            .Where(role => user.RoleIds.Contains(role.Id))
            .Select(role => role.Position)
            .DefaultIfEmpty(0)
            .Max();
        return new RoleMenuActorSnapshot(
            user.GuildPermissions.ManageRoles,
            hierarchy,
            user.Guild.OwnerId == user.Id);
    }

    internal static string FormatReconciliation(
        RoleMenuSelectionReconciliation reconciliation,
        IReadOnlyDictionary<ulong, string> roleNames)
    {
        var lines = new List<string>();
        AddRoleList(lines, "Added", reconciliation.AddedRoleIds, roleNames);
        AddRoleList(lines, "Removed", reconciliation.RemovedRoleIds, roleNames);
        AddRoleList(
            lines,
            "Still missing",
            reconciliation.MissingSelectedRoleIds,
            roleNames);
        AddRoleList(
            lines,
            "Still assigned",
            reconciliation.StillAssignedUnselectedRoleIds,
            roleNames);
        if (lines.Count == 0)
        {
            lines.Add("Discord's current role state already matches your selection.");
        }

        lines.Add(reconciliation.IsComplete
            ? "Bean Bot rechecked Discord's current role state. No roles outside this menu were changed."
            : "Bean Bot rechecked Discord's current role state, but some requested changes are still " +
              "not applied. No roles outside this menu were changed.");
        return BoundResponseContent(string.Join('\n', lines));
    }

    internal static string FormatSynchronizationResult(
        RoleMenuSynchronizationResult result,
        IReadOnlyDictionary<ulong, string> roleNames)
    {
        var lines = new List<string>();
        AddRoleList(lines, "Added", result.AddedRoleIds, roleNames);
        AddRoleList(lines, "Removed", result.RemovedRoleIds, roleNames);
        foreach (var failureGroup in result.Failures.GroupBy(failure => failure.Action))
        {
            AddRoleList(
                lines,
                $"Discord reported an error while trying to {failureGroup.Key}",
                failureGroup.Select(failure => failure.RoleId).ToList(),
                roleNames);
        }

        AddRoleList(
            lines,
            "Kept assigned because the replacement could not be added",
            result.SkippedRemovalRoleIds,
            roleNames);
        if (result.Interruption is not null)
        {
            var outcome = result.Interruption.Kind == RoleMenuMutationInterruptionKind.OutcomeUnknown
                ? "Discord may or may not have completed this operation"
                : "this operation was not attempted";
            lines.Add(
                $"**Interrupted while trying to {result.Interruption.Action}:** " +
                $"{GetRoleName(roleNames, result.Interruption.RoleId)} ({outcome}).");
        }

        if (lines.Count == 0)
        {
            return "Your role choices were already up to date. No roles outside this menu were changed.";
        }

        lines.Add(result.IsComplete
            ? "No roles outside this menu were changed."
            : "Bean Bot could not fully recheck the final role state. Open the menu again to " +
              "confirm it before retrying; no roles outside this menu were targeted.");
        return BoundResponseContent(string.Join('\n', lines));
    }

    private static void AddRoleList(
        List<string> lines,
        string label,
        IReadOnlyCollection<ulong> roleIds,
        IReadOnlyDictionary<ulong, string> roleNames)
    {
        if (roleIds.Count == 0)
        {
            return;
        }

        var names = roleIds.Select(roleId => GetRoleName(roleNames, roleId));
        lines.Add($"**{label} ({roleIds.Count}):** {string.Join(", ", names)}");
    }

    private static string BoundResponseContent(string content)
    {
        if (content.Length <= RoleMenuConstants.MaximumResponseContentLength)
        {
            return content;
        }

        const string suffix =
            "…\nSome role details were omitted. Open the menu again to verify the current state.";
        var cutoff = RoleMenuConstants.MaximumResponseContentLength - suffix.Length;
        if (cutoff > 0 && char.IsHighSurrogate(content[cutoff - 1]))
        {
            cutoff--;
        }

        return content[..cutoff] + suffix;
    }

    private static string GetRoleName(
        IReadOnlyDictionary<ulong, string> roleNames,
        ulong roleId)
    {
        var name = roleNames.TryGetValue(roleId, out var resolvedName)
            ? resolvedName
            : "unknown role";
        var normalized = string.Join(' ', name.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        normalized = RoleMenuText.TruncateWithEllipsis(normalized, 40);
        return normalized.Replace("`", "ʼ", StringComparison.Ordinal);
    }

    private async Task RespondToInvalidPrivateComponentAsync(
        string message,
        RequestOptions requestOptions,
        CancellationToken cancellationToken)
    {
        if (Context.Interaction is SocketMessageComponent component && IsEphemeral(component))
        {
            await component.UpdateAsync(
                properties => SetMessage(properties, message, MessageComponent.Empty),
                requestOptions);
            return;
        }

        if (Context.Interaction is SocketMessageComponent publicComponent)
        {
            await publicComponent.DeferLoadingAsync(ephemeral: true, requestOptions);
            await ReplaceResponseAsync(message, cancellationToken);
            return;
        }

        await RespondAsync(
            message,
            ephemeral: true,
            allowedMentions: AllowedMentions.None,
            options: requestOptions);
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

    private static RequestOptions CreateRequestOptions(CancellationToken cancellationToken)
        => new() { CancelToken = cancellationToken };

    private static bool IsEphemeral(SocketMessageComponent component)
        => component.Message.Flags?.HasFlag(MessageFlags.Ephemeral) == true;
}
