using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using static BeanBot.Discord.RoleMenus.DiscordRoleMenuClient;

namespace BeanBot.Discord.RoleMenus;

public abstract class RoleMenuModuleBase : InteractionModuleBase<SocketInteractionContext>
{
    protected RoleMenuModuleBase(RoleMenuInteractionService roleMenus)
    {
        RoleMenus = roleMenus ?? throw new ArgumentNullException(nameof(roleMenus));
    }

    protected RoleMenuInteractionService RoleMenus { get; }

    protected async Task<bool> AcknowledgeEphemeralComponentAsync(
        string loadingMessage,
        CancellationToken cancellationToken)
    {
        if (Context.Interaction is not SocketMessageComponent component
            || !IsEphemeral(component))
        {
            await RespondToInvalidComponentAsync(
                "These controls have expired. Run the command again.",
                cancellationToken);
            return false;
        }

        await RoleMenus.ExecuteInitialResponseAsync(
            supportsOriginalResponse: true,
            operationToken => component.UpdateAsync(
                properties => SetMessage(
                    properties,
                    loadingMessage,
                    null,
                    MessageComponent.Empty),
                CreateRequestOptions(operationToken)),
            operationToken => ReplaceResponseAsync(
                loadingMessage,
                operationToken),
            cancellationToken);
        return true;
    }

    protected async Task RespondToInvalidComponentAsync(
        string message,
        CancellationToken cancellationToken)
    {
        if (Context.Interaction is SocketMessageComponent component
            && IsEphemeral(component))
        {
            await RoleMenus.ExecuteInitialResponseAsync(
                supportsOriginalResponse: true,
                operationToken => component.UpdateAsync(
                    properties => SetMessage(
                        properties,
                        message,
                        null,
                        MessageComponent.Empty),
                    CreateRequestOptions(operationToken)),
                operationToken => ReplaceResponseAsync(message, operationToken),
                cancellationToken);
            return;
        }

        if (Context.Interaction is SocketMessageComponent publicComponent)
        {
            await RoleMenus.ExecuteInitialResponseAsync(
                supportsOriginalResponse: true,
                operationToken => publicComponent.DeferLoadingAsync(
                    ephemeral: true,
                    CreateRequestOptions(operationToken)),
                operationToken => ReplaceResponseAsync(message, operationToken),
                cancellationToken);
            await ReplaceResponseAsync(message, cancellationToken);
            return;
        }

        await RoleMenus.ExecuteInitialResponseAsync(
            supportsOriginalResponse: true,
            operationToken => RespondAsync(
                message,
                ephemeral: true,
                allowedMentions: AllowedMentions.None,
                options: CreateRequestOptions(operationToken)),
            operationToken => ReplaceResponseAsync(message, operationToken),
            cancellationToken);
    }

    protected async Task SendFreshFeedbackAsync(
        string content,
        MessageComponent? components = null)
    {
        if (RoleMenus.IsShuttingDown)
        {
            throw new OperationCanceledException();
        }

        using var feedbackCancellation = RoleMenus.CreateFeedbackCancellation();
        await ReplaceResponseAsync(
            content, feedbackCancellation.Token, components: components);
    }

    protected Task<IUserMessage> ReplaceResponseAsync(
        string content,
        CancellationToken cancellationToken,
        Embed? embed = null,
        MessageComponent? components = null)
        => ModifyOriginalResponseAsync(
            properties => SetMessage(
                properties,
                content,
                embed,
                components ?? MessageComponent.Empty),
            CreateRequestOptions(cancellationToken));

    protected static void SetMessage(
        MessageProperties properties,
        string content,
        Embed? embed,
        MessageComponent components)
    {
        properties.Content = content;
        Embed[] embeds = embed is null ? [] : [embed];
        properties.Embeds = embeds;
        properties.Components = components;
        properties.AllowedMentions = AllowedMentions.None;
    }

    protected static bool IsEphemeral(SocketMessageComponent component)
        => component.Message.Flags?.HasFlag(MessageFlags.Ephemeral) == true;

}
