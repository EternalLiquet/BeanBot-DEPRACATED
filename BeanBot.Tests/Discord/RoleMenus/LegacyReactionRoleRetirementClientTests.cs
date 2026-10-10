using System.Net;
using System.Reflection;
using BeanBot.Discord.RoleMenus;
using Discord;
using Discord.Net;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class LegacyReactionRoleRetirementClientTests
{
    private static readonly LegacyReactionRoleSource Source = new(1, 2, 3, [4]);

    [Fact]
    public async Task ReadPanel_RecognizesMatchingLegacyMessageAndDeleteRemovesItOnce()
    {
        var deletes = 0;
        var message = Message(3, 99,
            [new EmbedBuilder().AddField("custom", "<@&4>")
                .WithFooter("Role Group: Games").Build()],
            () =>
            {
                deletes++;
                return Task.CompletedTask;
            });
        var channel = Channel(1, 2, message);
        var client = Client(channel);

        var lookup = await client.ReadPanelAsync(Source, 99, CancellationToken.None);
        var deleted = await client.DeletePanelAsync(
            Assert.IsType<LegacyReactionRolePanelSnapshot>(lookup.Panel),
            99, Source.RoleIds, CancellationToken.None);

        Assert.Equal(LegacyReactionRolePanelLookupStatus.Found, lookup.Status);
        Assert.Equal("Games", lookup.SuggestedTitle);
        Assert.True(deleted);
        Assert.Equal(1, deletes);
    }

    [Fact]
    public async Task ReadPanel_DistinguishesDeletedChannelMessageAndWrongGuild()
    {
        var missingChannel = await Client(null).ReadPanelAsync(Source, 99,
            CancellationToken.None);
        var missingMessage = await Client(Channel(1, 2, null))
            .ReadPanelAsync(Source, 99, CancellationToken.None);
        var wrongGuild = await Client(Channel(9, 2, null))
            .ReadPanelAsync(Source, 99, CancellationToken.None);

        Assert.Equal(LegacyReactionRolePanelLookupStatus.ChannelMissing,
            missingChannel.Status);
        Assert.Equal(LegacyReactionRolePanelLookupStatus.MessageMissing,
            missingMessage.Status);
        Assert.Equal(LegacyReactionRolePanelLookupStatus.UnexpectedChannel,
            wrongGuild.Status);
    }

    [Fact]
    public async Task DeletePanel_LeavesMismatchedBotMessageUntouched()
    {
        var deletes = 0;
        var message = Message(3, 98,
            [new EmbedBuilder().AddField("custom", "<@&4>")
                .WithFooter("Role Group: Games").Build()],
            () =>
            {
                deletes++;
                return Task.CompletedTask;
            });
        var client = Client(Channel(1, 2, message));
        var panel = new LegacyReactionRolePanelSnapshot(1, 2, 3, 99, []);

        var deleted = await client.DeletePanelAsync(panel, 99, [4],
            CancellationToken.None);

        Assert.False(deleted);
        Assert.Equal(0, deletes);
    }

    [Fact]
    public async Task DeletePanel_TreatsMissingMessageOrChannelAsAlreadyGone()
    {
        var panel = new LegacyReactionRolePanelSnapshot(1, 2, 3, 99, []);
        var missingChannel = await Client(null).DeletePanelAsync(panel, 99, [4],
            CancellationToken.None);
        var missingMessage = await Client(Channel(1, 2, null))
            .DeletePanelAsync(panel, 99, [4], CancellationToken.None);
        var wrongGuild = await Client(Channel(9, 2, null))
            .DeletePanelAsync(panel, 99, [4], CancellationToken.None);

        Assert.True(missingChannel);
        Assert.True(missingMessage);
        Assert.False(wrongGuild);
    }

    [Fact]
    public async Task ReadPanel_RejectsMessageWithWrongLegacyShape()
    {
        var message = Message(3, 99,
            [new EmbedBuilder().WithFooter("Another panel").Build()],
            () => Task.CompletedTask);
        var result = await Client(Channel(1, 2, message))
            .ReadPanelAsync(Source, 99, CancellationToken.None);

        Assert.Equal(LegacyReactionRolePanelLookupStatus.Unrecognized,
            result.Status);
    }

    [Fact]
    public async Task AdministratorPermission_MissingUserStaysUnknown()
    {
        var result = await Client(null).CanAdministratorManageRolesAsync(
            1, 5, CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task NotFoundReadsAndDeleteTreatOnlyDefiniteAbsenceAsMissing()
    {
        var missing = new HttpException(HttpStatusCode.NotFound, null);
        var channelNotFound = new LegacyReactionRoleRetirementClient(
            (_, _) => Task.FromException<IChannel?>(missing),
            (_, _, _) => Task.FromException<IGuildUser?>(missing));
        var messageNotFound = Client(Channel(1, 2, null,
            messageReadFails: true));
        var panel = new LegacyReactionRolePanelSnapshot(1, 2, 3, 99, []);

        Assert.Equal(LegacyReactionRolePanelLookupStatus.ChannelMissing,
            (await channelNotFound.ReadPanelAsync(Source, 99,
                CancellationToken.None)).Status);
        Assert.Equal(LegacyReactionRolePanelLookupStatus.MessageMissing,
            (await messageNotFound.ReadPanelAsync(Source, 99,
                CancellationToken.None)).Status);
        Assert.Null(await channelNotFound.CanAdministratorManageRolesAsync(
            1, 5, CancellationToken.None));
        Assert.True(await channelNotFound.DeletePanelAsync(panel, 99, [4],
            CancellationToken.None));
        Assert.True(await messageNotFound.DeletePanelAsync(panel, 99, [4],
            CancellationToken.None));
    }

    [Fact]
    public async Task AdministratorPermission_UsesFreshGuildUser()
    {
        var guild = Proxy<IGuild>((method, _) => method.Name switch
        {
            "get_Id" => 1UL,
            _ => throw new NotSupportedException(method.Name)
        });
        var administrator = Proxy<IGuildUser>((method, _) => method.Name switch
        {
            "get_Guild" => guild,
            "get_GuildPermissions" => new GuildPermissions(manageRoles: true),
            _ => throw new NotSupportedException(method.Name)
        });
        var client = new LegacyReactionRoleRetirementClient(
            (_, _) => Task.FromResult<IChannel?>(null),
            (_, _, _) => Task.FromResult<IGuildUser?>(administrator));

        Assert.True(await client.CanAdministratorManageRolesAsync(1, 5,
            CancellationToken.None));
    }

    private static LegacyReactionRoleRetirementClient Client(IChannel? channel)
        => new((_, _) => Task.FromResult(channel),
            (_, _, _) => Task.FromResult<IGuildUser?>(null));

    private static ITextChannel Channel(ulong guildId, ulong channelId,
        IMessage? message, bool messageReadFails = false)
        => Proxy<ITextChannel>((method, _) => method.Name switch
        {
            "get_GuildId" => guildId,
            "get_Id" => channelId,
            "GetMessageAsync" => messageReadFails
                ? Task.FromException<IMessage?>(new HttpException(
                    HttpStatusCode.NotFound, null))
                : Task.FromResult(message),
            _ => throw new NotSupportedException(method.Name)
        });

    private static IMessage Message(ulong messageId, ulong authorId,
        IReadOnlyCollection<IEmbed> embeds, Func<Task> delete)
    {
        var author = Proxy<IUser>((method, _) => method.Name switch
        {
            "get_Id" => authorId,
            _ => throw new NotSupportedException(method.Name)
        });
        return Proxy<IMessage>((method, _) => method.Name switch
        {
            "get_Id" => messageId,
            "get_Author" => author,
            "get_Embeds" => embeds,
            "DeleteAsync" => delete(),
            _ => throw new NotSupportedException(method.Name)
        });
    }

    private static T Proxy<T>(Func<MethodInfo, object?[], object?> handler)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, StubProxy>();
        ((StubProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    public class StubProxy : DispatchProxy
    {
        internal Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => Handler(targetMethod!, args ?? []);
    }
}
