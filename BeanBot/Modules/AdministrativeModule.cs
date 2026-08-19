using System.Globalization;
using BeanBot.Attributes;
using BeanBot.Entities;
using BeanBot.Services;
using BeanBot.Util;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace BeanBot.Modules;

[Name("Administrative Commands")]
public class AdministrativeModule : ModuleBase<SocketCommandContext>
{
    internal enum RoleResolutionStatus
    {
        Resolved,
        NotFound,
        MultipleMentions,
        AmbiguousName
    }

    internal readonly record struct RoleCandidate(ulong Id, string Name);

    internal readonly record struct RoleResolution(RoleResolutionStatus Status, ulong? RoleId);

    private const int MaximumRolesPerGroup = 25;
    private static readonly TimeSpan InteractionTimeout = TimeSpan.FromSeconds(60);
    private readonly RoleReactService _roleReactService;
    private readonly DiscordMessageCleanupService _messageCleanupService;
    private readonly DiscordMessageWaiter _messageWaiter;
    private readonly ILogger<AdministrativeModule> _logger;

    public AdministrativeModule(
        RoleReactService roleReactService,
        DiscordMessageCleanupService messageCleanupService,
        DiscordMessageWaiter messageWaiter,
        ILogger<AdministrativeModule> logger)
    {
        _roleReactService = roleReactService ?? throw new ArgumentNullException(nameof(roleReactService));
        _messageCleanupService = messageCleanupService ?? throw new ArgumentNullException(nameof(messageCleanupService));
        _messageWaiter = messageWaiter ?? throw new ArgumentNullException(nameof(messageWaiter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [Command("role setting", RunMode = RunMode.Async)]
    [Summary("Will create a message for auto-role based on reactions")]
    [Alias("rolesetting", "role settings", "rolesettings")]
    [Remarks("role setting")]
    [RequireGuild]
    [RequireUserPermission(GuildPermission.ManageRoles)]
    [RequireBotPermission(GuildPermission.EmbedLinks)]
    public Task RoleSetting() => InvokeRoleSettingsAsync();

    internal async Task InvokeRoleSettingsAsync()
    {
        var messagesInInteraction = new List<IMessage> { Context.Message };
        try
        {
            var roleEmotePairs = new List<RoleEmotePair>();
            messagesInInteraction.Add(await ReplyAsync($"How many roles do you wish to configure? (1-{MaximumRolesPerGroup})"));
            var amountMessage = await _messageWaiter.WaitForNextMessageAsync(Context, InteractionTimeout);
            var roleCountResult = await GetRoleCountAsync(messagesInInteraction, amountMessage);
            if (!roleCountResult.Success)
            {
                return;
            }

            for (var index = 0; index < roleCountResult.RoleCount; index++)
            {
                messagesInInteraction.Add(await ReplyAsync("Which role would you like to set up?"));
                var roleMessage = await _messageWaiter.WaitForNextMessageAsync(Context, InteractionTimeout);
                var role = await GetRoleAsync(messagesInInteraction, roleMessage);
                if (role == null)
                {
                    return;
                }

                messagesInInteraction.Add(await ReplyAsync($"Which emote would you like to set up with the role {role.Name}?"));
                var emoteMessage = await _messageWaiter.WaitForNextMessageAsync(Context, InteractionTimeout);
                var emote = await GetEmoteAsync(messagesInInteraction, emoteMessage);
                if (emote == null)
                {
                    return;
                }

                if (roleEmotePairs.Any(pair =>
                    pair.RoleId == role.Id.ToString(CultureInfo.InvariantCulture)
                    || pair.EmojiId == emote.Id.ToString(CultureInfo.InvariantCulture)))
                {
                    messagesInInteraction.Add(await ReplyAsync("That role or emote is already being configured. Please start again."));
                    return;
                }

                roleEmotePairs.Add(new RoleEmotePair(
                    role.Id.ToString(CultureInfo.InvariantCulture),
                    emote.Id.ToString(CultureInfo.InvariantCulture)));
            }

            messagesInInteraction.Add(await ReplyAsync("Please label this group of roles (i.e. Games, Position, NSFW, etc)."));
            var labelMessage = await _messageWaiter.WaitForNextMessageAsync(Context, InteractionTimeout);
            if (labelMessage == null)
            {
                messagesInInteraction.Add(await ReplyAsync("Time has expired, please try again."));
                return;
            }

            messagesInInteraction.Add(labelMessage);
            await ReactionRoleSetupTransaction.ExecuteAsync(
                () => CreateRoleMessageAsync(roleEmotePairs, labelMessage.Content),
                async messageToListen =>
                {
                    await AddRoleReactionsAsync(messageToListen, roleEmotePairs);
                    await _roleReactService.SaveRoleSettings(roleEmotePairs, messageToListen);
                },
                messageToListen => messageToListen.DeleteAsync(),
                exception => BeanBotLog.IncompleteReactionRoleCleanupFailed(_logger, exception));
        }
        finally
        {
            await CleanUpMessagesAsync(messagesInInteraction);
        }
    }

    private async Task<IUserMessage> CreateRoleMessageAsync(IEnumerable<RoleEmotePair> roleEmotePairs, string roleGroupLabel)
    {
        var pairs = roleEmotePairs.ToList();
        var roleEmbed = new EmbedBuilder();
        foreach (var pair in pairs)
        {
            var emote = Context.Guild.Emotes.First(candidate =>
                candidate.Id.ToString(CultureInfo.InvariantCulture) == pair.EmojiId);
            roleEmbed.AddField(emote.ToString(), $"<@&{pair.RoleId}>", inline: true);
        }

        roleEmbed.WithFooter(footer => footer.Text = $"Role Group: {roleGroupLabel}");
        return await ReplyAsync(embed: roleEmbed.Build());
    }

    private async Task AddRoleReactionsAsync(IUserMessage messageToListen, IEnumerable<RoleEmotePair> roleEmotePairs)
    {
        var pairs = roleEmotePairs.ToList();
        foreach (var pair in pairs)
        {
            var emote = Context.Guild.Emotes.First(candidate =>
                candidate.Id.ToString(CultureInfo.InvariantCulture) == pair.EmojiId);
            await messageToListen.AddReactionAsync(emote);
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }
    }

    private async Task<(bool Success, int RoleCount)> GetRoleCountAsync(List<IMessage> messages, SocketMessage? response)
    {
        if (response == null)
        {
            messages.Add(await ReplyAsync("Time has expired, please try again."));
            return (false, 0);
        }

        messages.Add(response);
        if (!int.TryParse(response.Content, out var roleCount) || roleCount < 1 || roleCount > MaximumRolesPerGroup)
        {
            messages.Add(await ReplyAsync($"Please enter a whole number from 1 to {MaximumRolesPerGroup}."));
            return (false, 0);
        }

        return (true, roleCount);
    }

    private async Task<SocketRole?> GetRoleAsync(List<IMessage> messages, SocketMessage? response)
    {
        if (response == null)
        {
            messages.Add(await ReplyAsync("Time has expired, please try again."));
            return null;
        }

        messages.Add(response);
        var availableRoles = Context.Guild.Roles.ToList();
        var roleResolution = ResolveRole(
            response.Content,
            response.MentionedRoleIds,
            availableRoles.Select(role => new RoleCandidate(role.Id, role.Name)));

        if (roleResolution.Status == RoleResolutionStatus.MultipleMentions)
        {
            messages.Add(await ReplyAsync("Please mention only one role. Please start again."));
            return null;
        }

        if (roleResolution.Status == RoleResolutionStatus.AmbiguousName)
        {
            messages.Add(await ReplyAsync("Multiple roles have that name. Please mention the role you want and start again."));
            return null;
        }

        if (roleResolution.Status == RoleResolutionStatus.NotFound)
        {
            messages.Add(await ReplyAsync($"The role {response.Content} does not exist. Please start again."));
            return null;
        }

        return availableRoles.Single(role => role.Id == roleResolution.RoleId);
    }

    internal static RoleResolution ResolveRole(
        string roleName,
        IEnumerable<ulong> mentionedRoleIds,
        IEnumerable<RoleCandidate> availableRoles)
    {
        var roleCandidates = availableRoles.ToList();
        var distinctMentionedRoleIds = mentionedRoleIds.Distinct().Take(2).ToList();
        if (distinctMentionedRoleIds.Count > 1)
        {
            return new RoleResolution(RoleResolutionStatus.MultipleMentions, null);
        }

        if (distinctMentionedRoleIds.Count == 1)
        {
            var mentionedRoleId = distinctMentionedRoleIds[0];
            return roleCandidates.Any(candidate => candidate.Id == mentionedRoleId)
                ? new RoleResolution(RoleResolutionStatus.Resolved, mentionedRoleId)
                : new RoleResolution(RoleResolutionStatus.NotFound, null);
        }

        if (string.IsNullOrWhiteSpace(roleName))
        {
            return new RoleResolution(RoleResolutionStatus.NotFound, null);
        }

        var normalizedRoleName = roleName.Trim();
        var matchingRoles = roleCandidates
            .Where(candidate => string.Equals(
                normalizedRoleName,
                candidate.Name.Trim(),
                StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();

        return matchingRoles.Count switch
        {
            1 => new RoleResolution(RoleResolutionStatus.Resolved, matchingRoles[0].Id),
            > 1 => new RoleResolution(RoleResolutionStatus.AmbiguousName, null),
            _ => new RoleResolution(RoleResolutionStatus.NotFound, null)
        };
    }

    private async Task<Emote?> GetEmoteAsync(List<IMessage> messages, SocketMessage? response)
    {
        if (response == null)
        {
            messages.Add(await ReplyAsync("Time has expired, please try again."));
            return null;
        }

        messages.Add(response);
        var emote = Context.Guild.Emotes.FirstOrDefault(candidate =>
            response.Content.Contains(candidate.Name, StringComparison.OrdinalIgnoreCase));
        if (emote == null)
        {
            messages.Add(await ReplyAsync($"The emote {response.Content} does not exist. Please start again."));
            return null;
        }

        return emote;
    }

    private async Task CleanUpMessagesAsync(List<IMessage> messages)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        if (Context.Channel is not ITextChannel textChannel || messages.Count == 0)
        {
            return;
        }

        try
        {
            await _messageCleanupService.DeleteAsync(textChannel, messages);
        }
        catch (Exception exception)
        {
            BeanBotLog.ReactionRoleCleanupFailed(_logger, exception);
        }
    }
}
