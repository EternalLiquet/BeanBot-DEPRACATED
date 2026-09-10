using System.Globalization;
using BeanBot.Logging;
using BeanBot.Persistence.Models;
using Discord;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;

namespace BeanBot.Discord.RoleMenus;

internal enum RoleMenuAuditStatus
{
    Healthy,
    Broken,
    Unknown
}

internal enum RoleMenuAuditIssueKind
{
    SettingsInvalid,
    GuildMismatch,
    BotMissing,
    BotSnapshotMismatch,
    ChannelMissingOrInvalid,
    ChannelSnapshotMismatch,
    BotMissingManageRoles,
    MissingViewChannel,
    MissingSendMessages,
    MissingEmbedLinks,
    MissingReadMessageHistory,
    PanelMissing,
    PanelGuildMismatch,
    PanelChannelMismatch,
    PanelMessageMismatch,
    PanelUnexpectedAuthor,
    PanelMissingManageButton,
    RoleMissing,
    RoleEveryone,
    RoleManaged,
    RoleBotHierarchy,
    LookupTimedOut,
    LookupFailed
}

internal readonly record struct RoleMenuAuditFinding(
    RoleMenuAuditIssueKind Kind,
    ulong? RoleId = null);

internal sealed record RoleMenuAuditResult(
    ObjectId MenuId,
    RoleMenuAuditStatus Status,
    IReadOnlyList<RoleMenuAuditFinding> Findings);

internal enum RoleMenuAuditEnvironmentStatus
{
    Found,
    BotMissing,
    ChannelMissingOrInvalid
}

internal readonly record struct RoleMenuAuditChannelSnapshot(
    ulong GuildId,
    ulong ChannelId,
    bool ViewChannel,
    bool SendMessages,
    bool EmbedLinks,
    bool ReadMessageHistory);

internal sealed record RoleMenuAuditEnvironmentResult(
    RoleMenuAuditEnvironmentStatus Status,
    RoleMenuBotSnapshot? Bot = null,
    RoleMenuAuditChannelSnapshot? Channel = null);

internal sealed record RoleMenuAuditOperations(
    Func<ulong, ulong, ulong, CancellationToken, Task<RoleMenuAuditEnvironmentResult>>
        ReadEnvironment,
    Func<ulong, ObjectId, ulong, ulong, CancellationToken, Task<RoleMenuPanelSnapshot?>>
        ReadPanel);

internal static class RoleMenuAuditWorkflow
{
    internal static async Task<RoleMenuAuditResult> ExecuteAsync(
        RoleMenuSettings settings,
        ulong guildId,
        ulong botUserId,
        RoleMenuAuditOperations operations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfZero(guildId);
        ArgumentOutOfRangeException.ThrowIfZero(botUserId);
        ArgumentNullException.ThrowIfNull(operations);
        ValidateOperations(operations);
        cancellationToken.ThrowIfCancellationRequested();

        if (settings.Id == ObjectId.Empty
            || !RoleMenuSettingsParser.TryParse(settings, out var parsed, out _))
        {
            return Broken(settings.Id, RoleMenuAuditIssueKind.SettingsInvalid);
        }

        if (parsed.GuildId != guildId)
        {
            return Broken(settings.Id, RoleMenuAuditIssueKind.GuildMismatch);
        }

        var environment = await operations.ReadEnvironment(
            guildId,
            botUserId,
            parsed.ChannelId,
            cancellationToken);
        if (environment.Status == RoleMenuAuditEnvironmentStatus.BotMissing)
        {
            return Broken(settings.Id, RoleMenuAuditIssueKind.BotMissing);
        }

        if (environment.Status == RoleMenuAuditEnvironmentStatus.ChannelMissingOrInvalid)
        {
            return Broken(settings.Id, RoleMenuAuditIssueKind.ChannelMissingOrInvalid);
        }

        var bot = environment.Bot
            ?? throw new InvalidOperationException(
                "A successful role-menu audit environment did not include the bot snapshot.");
        var channel = environment.Channel
            ?? throw new InvalidOperationException(
                "A successful role-menu audit environment did not include the channel snapshot.");
        if (bot.GuildId != guildId || bot.UserId != botUserId || bot.Roles is null)
        {
            return Broken(settings.Id, RoleMenuAuditIssueKind.BotSnapshotMismatch);
        }

        if (channel.GuildId != guildId || channel.ChannelId != parsed.ChannelId)
        {
            return Broken(settings.Id, RoleMenuAuditIssueKind.ChannelSnapshotMismatch);
        }

        var findings = new List<RoleMenuAuditFinding>();
        AddChannelPermissionFindings(channel, findings);

        var panel = await operations.ReadPanel(
            guildId,
            settings.Id,
            parsed.ChannelId,
            parsed.MessageId,
            cancellationToken);
        if (panel is null)
        {
            findings.Add(new RoleMenuAuditFinding(RoleMenuAuditIssueKind.PanelMissing));
        }
        else
        {
            AddPanelFinding(parsed, guildId, botUserId, panel, findings);
        }

        var roleValidation = RoleMenuRoleValidator.Validate(
            parsed.RoleIds,
            bot.Roles,
            bot.Actor);
        foreach (var issue in roleValidation.Issues)
        {
            var finding = MapRoleIssue(issue);
            if (finding is RoleMenuAuditFinding roleFinding)
            {
                findings.Add(roleFinding);
            }
        }

        return findings.Count == 0
            ? new RoleMenuAuditResult(settings.Id, RoleMenuAuditStatus.Healthy, [])
            : new RoleMenuAuditResult(settings.Id, RoleMenuAuditStatus.Broken, findings);
    }

    private static void AddChannelPermissionFindings(
        RoleMenuAuditChannelSnapshot channel,
        ICollection<RoleMenuAuditFinding> findings)
    {
        if (!channel.ViewChannel)
        {
            findings.Add(new RoleMenuAuditFinding(RoleMenuAuditIssueKind.MissingViewChannel));
        }

        if (!channel.SendMessages)
        {
            findings.Add(new RoleMenuAuditFinding(RoleMenuAuditIssueKind.MissingSendMessages));
        }

        if (!channel.EmbedLinks)
        {
            findings.Add(new RoleMenuAuditFinding(RoleMenuAuditIssueKind.MissingEmbedLinks));
        }

        if (!channel.ReadMessageHistory)
        {
            findings.Add(new RoleMenuAuditFinding(RoleMenuAuditIssueKind.MissingReadMessageHistory));
        }
    }

    private static void AddPanelFinding(
        ParsedRoleMenuSettings settings,
        ulong guildId,
        ulong botUserId,
        RoleMenuPanelSnapshot panel,
        ICollection<RoleMenuAuditFinding> findings)
    {
        if (panel.GuildId != guildId)
        {
            findings.Add(new RoleMenuAuditFinding(RoleMenuAuditIssueKind.PanelGuildMismatch));
            return;
        }

        if (panel.ChannelId != settings.ChannelId)
        {
            findings.Add(new RoleMenuAuditFinding(RoleMenuAuditIssueKind.PanelChannelMismatch));
            return;
        }

        var issue = RoleMenuPanelContextValidator.Validate(
            settings,
            guildId,
            settings.ChannelId,
            panel.MessageId,
            panel.AuthorId,
            botUserId,
            panel.HasManageButton);
        var finding = issue switch
        {
            RoleMenuPanelContextIssue.None => null,
            RoleMenuPanelContextIssue.GuildMismatch => RoleMenuAuditIssueKind.PanelGuildMismatch,
            RoleMenuPanelContextIssue.ChannelMismatch => RoleMenuAuditIssueKind.PanelChannelMismatch,
            RoleMenuPanelContextIssue.MessageMismatch => RoleMenuAuditIssueKind.PanelMessageMismatch,
            RoleMenuPanelContextIssue.UnexpectedAuthor => RoleMenuAuditIssueKind.PanelUnexpectedAuthor,
            RoleMenuPanelContextIssue.MissingManageButton => RoleMenuAuditIssueKind.PanelMissingManageButton,
            _ => throw new InvalidOperationException("Unknown role-menu panel validation result.")
        };
        if (finding is RoleMenuAuditIssueKind issueKind)
        {
            findings.Add(new RoleMenuAuditFinding(issueKind));
        }
    }

    private static RoleMenuAuditFinding? MapRoleIssue(RoleMenuRoleIssue issue)
    {
        var kind = issue.Kind switch
        {
            RoleMenuRoleIssueKind.BotMissingManageRoles =>
                RoleMenuAuditIssueKind.BotMissingManageRoles,
            RoleMenuRoleIssueKind.Missing => RoleMenuAuditIssueKind.RoleMissing,
            RoleMenuRoleIssueKind.Everyone => RoleMenuAuditIssueKind.RoleEveryone,
            RoleMenuRoleIssueKind.Managed => RoleMenuAuditIssueKind.RoleManaged,
            RoleMenuRoleIssueKind.BotHierarchy => RoleMenuAuditIssueKind.RoleBotHierarchy,
            RoleMenuRoleIssueKind.Duplicate => RoleMenuAuditIssueKind.SettingsInvalid,
            RoleMenuRoleIssueKind.AdministratorMissingManageRoles => null,
            RoleMenuRoleIssueKind.AdministratorHierarchy => null,
            _ => throw new InvalidOperationException("Unknown role-menu role validation result.")
        };
        return kind is RoleMenuAuditIssueKind issueKind
            ? new RoleMenuAuditFinding(issueKind, issue.RoleId)
            : null;
    }

    private static RoleMenuAuditResult Broken(
        ObjectId menuId,
        RoleMenuAuditIssueKind issue)
        => new(
            menuId,
            RoleMenuAuditStatus.Broken,
            [new RoleMenuAuditFinding(issue)]);

    private static void ValidateOperations(RoleMenuAuditOperations operations)
    {
        ArgumentNullException.ThrowIfNull(operations.ReadEnvironment);
        ArgumentNullException.ThrowIfNull(operations.ReadPanel);
    }
}

internal static class RoleMenuAuditor
{
    internal static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(3);
    internal const int MaximumConcurrentAudits = 4;

    internal static Task<RoleMenuAuditResult> AuditAsync(
        DiscordRoleMenuClient discord,
        RoleMenuSettings settings,
        ulong guildId,
        ulong botUserId,
        ILogger logger,
        CancellationToken cancellationToken)
        => AuditAsync(
            settings,
            guildId,
            botUserId,
            CreateOperations(discord),
            logger,
            LookupTimeout,
            cancellationToken);

    internal static Task<IReadOnlyList<RoleMenuAuditResult>> AuditManyAsync(
        DiscordRoleMenuClient discord,
        IReadOnlyList<RoleMenuSettings> settings,
        ulong guildId,
        ulong botUserId,
        ILogger logger,
        CancellationToken cancellationToken)
        => AuditManyAsync(
            settings,
            guildId,
            botUserId,
            CreateOperations(discord),
            logger,
            LookupTimeout,
            cancellationToken);

    internal static async Task<IReadOnlyList<RoleMenuAuditResult>> AuditManyAsync(
        IReadOnlyList<RoleMenuSettings> settings,
        ulong guildId,
        ulong botUserId,
        RoleMenuAuditOperations operations,
        ILogger logger,
        TimeSpan lookupTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lookupTimeout, TimeSpan.Zero);
        var limitedSettings = settings.Take(RoleMenuConstants.MaximumListedMenus).ToList();
        var results = new List<RoleMenuAuditResult>(limitedSettings.Count);
        for (var offset = 0; offset < limitedSettings.Count; offset += MaximumConcurrentAudits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batchSize = Math.Min(
                MaximumConcurrentAudits,
                limitedSettings.Count - offset);
            var batch = new Task<RoleMenuAuditResult>[batchSize];
            for (var index = 0; index < batchSize; index++)
            {
                batch[index] = AuditAsync(
                    limitedSettings[offset + index],
                    guildId,
                    botUserId,
                    operations,
                    logger,
                    lookupTimeout,
                    cancellationToken);
            }

            results.AddRange(await Task.WhenAll(batch));
        }

        return results;
    }

    internal static async Task<RoleMenuAuditResult> AuditAsync(
        RoleMenuSettings settings,
        ulong guildId,
        ulong botUserId,
        RoleMenuAuditOperations operations,
        ILogger logger,
        TimeSpan lookupTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lookupTimeout, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();

        using var lookupCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        lookupCancellation.CancelAfter(lookupTimeout);
        try
        {
            return await RoleMenuAuditWorkflow.ExecuteAsync(
                settings,
                guildId,
                botUserId,
                operations,
                lookupCancellation.Token);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested
                  && lookupCancellation.IsCancellationRequested)
        {
            BeanBotLog.RoleMenuAuditLookupFailed(
                logger,
                settings.Id.ToString(),
                exception);
            return Unknown(settings.Id, RoleMenuAuditIssueKind.LookupTimedOut);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            BeanBotLog.RoleMenuAuditLookupFailed(
                logger,
                settings.Id.ToString(),
                exception);
            return Unknown(settings.Id, RoleMenuAuditIssueKind.LookupFailed);
        }
    }

    private static RoleMenuAuditOperations CreateOperations(DiscordRoleMenuClient discord)
    {
        ArgumentNullException.ThrowIfNull(discord);
        return new RoleMenuAuditOperations(
            async (guildId, botUserId, channelId, cancellationToken) =>
            {
                var requestOptions = DiscordRoleMenuClient.CreateRequestOptions(cancellationToken);
                var bot = await discord.GetGuildUserAsync(
                    guildId,
                    botUserId,
                    requestOptions);
                if (bot is null)
                {
                    return new RoleMenuAuditEnvironmentResult(
                        RoleMenuAuditEnvironmentStatus.BotMissing);
                }

                var channel = await discord.GetGuildTextChannelAsync(
                    guildId,
                    channelId,
                    requestOptions);
                if (channel is null)
                {
                    return new RoleMenuAuditEnvironmentResult(
                        RoleMenuAuditEnvironmentStatus.ChannelMissingOrInvalid);
                }

                var permissions = bot.GetPermissions(channel);
                return new RoleMenuAuditEnvironmentResult(
                    RoleMenuAuditEnvironmentStatus.Found,
                    new RoleMenuBotSnapshot(
                        bot.Guild.Id,
                        bot.Id,
                        DiscordRoleMenuClient.CreateRoleSnapshots(bot),
                        DiscordRoleMenuClient.CreateActorSnapshot(bot)),
                    new RoleMenuAuditChannelSnapshot(
                        channel.GuildId,
                        channel.Id,
                        permissions.ViewChannel,
                        permissions.SendMessages,
                        permissions.EmbedLinks,
                        permissions.ReadMessageHistory));
            },
            (guildId, menuId, channelId, messageId, cancellationToken) =>
                discord.ReadPanelSnapshotAsync(
                    guildId,
                    menuId,
                    channelId,
                    messageId,
                    cancellationToken));
    }

    private static RoleMenuAuditResult Unknown(
        ObjectId menuId,
        RoleMenuAuditIssueKind issue)
        => new(
            menuId,
            RoleMenuAuditStatus.Unknown,
            [new RoleMenuAuditFinding(issue)]);
}

internal static class RoleMenuAuditPresentation
{
    private const int MaximumDisplayedFindings = 8;

    internal static Embed BuildEmbed(
        IReadOnlyCollection<RoleMenuAuditResult> results,
        bool hasMore)
    {
        ArgumentNullException.ThrowIfNull(results);
        var lines = results.Select(FormatResult).ToList();
        if (hasMore)
        {
            lines.Add(
                $"Only the {RoleMenuConstants.MaximumListedMenus.ToString(CultureInfo.InvariantCulture)} " +
                "newest saved menus were audited. Use the stable panel footer ID with " +
                "`/role-menu audit menu-id:<id>` for an older panel.");
        }

        return new EmbedBuilder()
            .WithTitle("Role-menu audit")
            .WithDescription(string.Join("\n", lines))
            .Build();
    }

    private static string FormatResult(RoleMenuAuditResult result)
    {
        var status = result.Status.ToString().ToUpperInvariant();
        if (result.Status == RoleMenuAuditStatus.Healthy)
        {
            return $"`{result.MenuId}` — **{status}** — all checks passed";
        }

        var findings = result.Findings
            .Take(MaximumDisplayedFindings)
            .Select(FormatFinding)
            .ToList();
        var hiddenCount = result.Findings.Count - findings.Count;
        var suffix = hiddenCount > 0
            ? $"; +{hiddenCount.ToString(CultureInfo.InvariantCulture)} more"
            : string.Empty;
        return $"`{result.MenuId}` — **{status}** — {string.Join("; ", findings)}{suffix}";
    }

    private static string FormatFinding(RoleMenuAuditFinding finding)
    {
        var roleId = finding.RoleId?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
        return finding.Kind switch
        {
            RoleMenuAuditIssueKind.SettingsInvalid =>
                "saved configuration is invalid; recreate the panel",
            RoleMenuAuditIssueKind.GuildMismatch =>
                "saved server identity does not match this server",
            RoleMenuAuditIssueKind.BotMissing =>
                "Bean Bot could not be confirmed as a server member",
            RoleMenuAuditIssueKind.BotSnapshotMismatch =>
                "Bean Bot identity snapshot did not match this server",
            RoleMenuAuditIssueKind.ChannelMissingOrInvalid =>
                "target channel is missing or is not a normal text channel",
            RoleMenuAuditIssueKind.ChannelSnapshotMismatch =>
                "target channel identity did not match saved configuration",
            RoleMenuAuditIssueKind.BotMissingManageRoles =>
                "Bean Bot is missing Manage Roles",
            RoleMenuAuditIssueKind.MissingViewChannel =>
                "target channel is missing View Channel",
            RoleMenuAuditIssueKind.MissingSendMessages =>
                "target channel is missing Send Messages",
            RoleMenuAuditIssueKind.MissingEmbedLinks =>
                "target channel is missing Embed Links",
            RoleMenuAuditIssueKind.MissingReadMessageHistory =>
                "target channel is missing Read Message History",
            RoleMenuAuditIssueKind.PanelMissing =>
                "saved panel message is missing",
            RoleMenuAuditIssueKind.PanelGuildMismatch =>
                "panel resolved to the wrong server",
            RoleMenuAuditIssueKind.PanelChannelMismatch =>
                "panel resolved to the wrong channel",
            RoleMenuAuditIssueKind.PanelMessageMismatch =>
                "saved message ID no longer matches the panel",
            RoleMenuAuditIssueKind.PanelUnexpectedAuthor =>
                "saved message is not owned by Bean Bot",
            RoleMenuAuditIssueKind.PanelMissingManageButton =>
                "saved message no longer has the expected role-menu control",
            RoleMenuAuditIssueKind.RoleMissing =>
                $"configured role `{roleId}` is missing",
            RoleMenuAuditIssueKind.RoleEveryone =>
                $"configured role `{roleId}` is @everyone and cannot be assigned",
            RoleMenuAuditIssueKind.RoleManaged =>
                $"configured role `{roleId}` is integration-managed",
            RoleMenuAuditIssueKind.RoleBotHierarchy =>
                $"configured role `{roleId}` is at or above Bean Bot's highest role",
            RoleMenuAuditIssueKind.LookupTimedOut =>
                "Discord checks timed out; retry the audit",
            RoleMenuAuditIssueKind.LookupFailed =>
                "Discord checks failed unexpectedly; retry the audit",
            _ => "an unknown audit finding was reported"
        };
    }
}
