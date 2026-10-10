using BeanBot.Discord.Interactions;
using BeanBot.Discord.RoleMenus;
using BeanBot.Persistence.Models;
using BeanBot.Persistence.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class RoleMenuInteractionServiceTests
{
    [Fact]
    public async Task DiscordDeletion_CleansOnlyTheExactSavedMessage_AndDuplicatesAreHarmless()
    {
        var fixture = CreateFixture();
        var settings = CreateSettings();
        await fixture.Repository.UpsertAsync(settings);

        Assert.Equal(0, await fixture.Service.DeleteSavedPanelsForMessageAsync(1, 2, 4, CancellationToken.None));
        Assert.Equal(0, await fixture.Service.DeleteSavedPanelsForMessageAsync(1, 9, 3, CancellationToken.None));
        Assert.NotNull(await fixture.Repository.GetAsync(settings.Id, "1"));

        Assert.Equal(1, await fixture.Service.DeleteSavedPanelsForMessageAsync(1, 2, 3, CancellationToken.None));
        Assert.Equal(0, await fixture.Service.DeleteSavedPanelsForMessageAsync(1, 2, 3, CancellationToken.None));
        Assert.Null(await fixture.Repository.GetAsync(settings.Id, "1"));
    }

    [Fact]
    public async Task DiscordDeletion_WaitingForRepair_CannotDeleteReplacementBinding()
    {
        var fixture = CreateFixture();
        var original = CreateSettings();
        await fixture.Repository.UpsertAsync(original);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repair = fixture.Service.RunMenuMutationAsync(original.Id, async token =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
            await fixture.Repository.UpsertAsync(new RoleMenuSettings(
                original.Id, "1", "2", "4", "Games", "", ["5"], RoleMenuSelectionMode.Multiple), token);
            return true;
        }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var deletion = fixture.Service.DeleteSavedPanelsForMessageAsync(1, 2, 3, CancellationToken.None);
        release.SetResult();
        await repair;

        Assert.Equal(0, await deletion);
        Assert.Equal("4", (await fixture.Repository.GetAsync(original.Id, "1"))?.MessageId);
    }

    [Fact]
    public async Task DiscordDeletion_WaitingForNewRevision_RemovesSameBinding()
    {
        var fixture = CreateFixture();
        var original = CreateSettings();
        await fixture.Repository.UpsertAsync(original);
        var mutationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowMutation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deletionReadOriginal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Store.OnGetByMessage = () => deletionReadOriginal.TrySetResult();

        var mutation = fixture.Service.RunMenuMutationAsync(original.Id, async token =>
        {
            mutationEntered.SetResult();
            await allowMutation.Task.WaitAsync(token);
            await fixture.Repository.UpsertAsync(new RoleMenuSettings(
                original.Id, "1", "2", "3", "Updated", "", ["5"],
                RoleMenuSelectionMode.Exclusive), token);
            return true;
        }, CancellationToken.None);
        await mutationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var deletion = fixture.Service.DeleteSavedPanelsForMessageAsync(1, 2, 3, CancellationToken.None);
        await deletionReadOriginal.Task.WaitAsync(TimeSpan.FromSeconds(2));
        allowMutation.SetResult();

        Assert.True(await mutation.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, await deletion.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Null(await fixture.Repository.GetAsync(original.Id, "1"));
    }

    [Fact]
    public void ModuleConstructors_RequireFacadeAndLogger()
    {
        var fixture = CreateFixture();
        var discord = new DiscordRoleMenuClient(
            (_, _, _) => Task.FromResult<global::Discord.IGuildUser?>(null),
            (_, _) => Task.FromResult<global::Discord.IChannel?>(null));

        var administration = new RoleMenuAdministrationService(
            fixture.Service, discord, NullLogger<RoleMenuAdministrationService>.Instance);
        var members = new RoleMenuMemberService(
            fixture.Service, discord, NullLogger<RoleMenuMemberService>.Instance);

        Assert.Throws<ArgumentNullException>(() => new RoleMenuAdminModule(
            null!,
            discord,
            administration,
            NullLogger<RoleMenuAdminModule>.Instance));
        Assert.Throws<ArgumentNullException>(() => new RoleMenuAdminModule(
            fixture.Service,
            discord,
            administration,
            null!));
        _ = new RoleMenuAdminModule(
            fixture.Service,
            discord,
            administration,
            NullLogger<RoleMenuAdminModule>.Instance);

        Assert.Throws<ArgumentNullException>(() => new RoleMenuMemberModule(
            null!,
            members,
            NullLogger<RoleMenuMemberModule>.Instance));
        Assert.Throws<ArgumentNullException>(() => new RoleMenuMemberModule(
            fixture.Service,
            members,
            null!));
        _ = new RoleMenuMemberModule(
            fixture.Service,
            members,
            NullLogger<RoleMenuMemberModule>.Instance);
    }

    [Fact]
    public void TryBeginDeletion_IgnoresRepeatedClicksFromTheSameAdministrator()
    {
        var fixture = CreateFixture();
        var menuId = ObjectId.GenerateNewId();
        var otherMenuId = ObjectId.GenerateNewId();

        Assert.True(fixture.Service.TryBeginDeletion(menuId, 3UL));
        Assert.False(fixture.Service.TryBeginDeletion(menuId, 3UL));
        Assert.True(fixture.Service.TryBeginDeletion(otherMenuId, 3UL));
        Assert.True(fixture.Service.TryBeginDeletion(menuId, 4UL));

        fixture.Service.EndDeletion(menuId, 3UL);

        Assert.True(fixture.Service.TryBeginDeletion(menuId, 3UL));
    }

    [Fact]
    public void Constructor_RejectsEveryMissingDependency()
    {
        var fixture = CreateFixture();

        Assert.Throws<ArgumentNullException>(() => new RoleMenuInteractionService(
            null!,
            fixture.Drafts,
            fixture.Coordinator,
            fixture.ExecutionContext));
        Assert.Throws<ArgumentNullException>(() => new RoleMenuInteractionService(
            fixture.Repository,
            null!,
            fixture.Coordinator,
            fixture.ExecutionContext));
        Assert.Throws<ArgumentNullException>(() => new RoleMenuInteractionService(
            fixture.Repository,
            fixture.Drafts,
            null!,
            fixture.ExecutionContext));
        Assert.Throws<ArgumentNullException>(() => new RoleMenuInteractionService(
            fixture.Repository,
            fixture.Drafts,
            fixture.Coordinator,
            null!));
    }

    [Fact]
    public void CancellationFactories_LinkTheCurrentInteractionExecution()
    {
        var fixture = CreateFixture();
        using var stopping = new CancellationTokenSource();
        using var scope = fixture.ExecutionContext.Enter(stopping.Token);
        using var operation = fixture.Service.CreateOperationCancellation();
        using var feedback = fixture.Service.CreateFeedbackCancellation();

        Assert.False(fixture.Service.IsShuttingDown);
        Assert.False(operation.IsCancellationRequested);
        Assert.False(feedback.IsCancellationRequested);

        stopping.Cancel();

        Assert.True(fixture.Service.IsShuttingDown);
        Assert.True(operation.IsCancellationRequested);
        Assert.True(feedback.IsCancellationRequested);
    }

    [Fact]
    public async Task ExecuteInitialResponseAsync_TracksAndReconcilesAnAmbiguousSend()
    {
        var fixture = CreateFixture();
        using var scope = fixture.ExecutionContext.Enter(CancellationToken.None);
        var initialFailure = new InvalidOperationException("send failed after commit");
        var reconciliationCalls = 0;
        CancellationToken initialResponseToken = default;

        await fixture.Service.ExecuteInitialResponseAsync(
            supportsOriginalResponse: true,
            operationToken =>
            {
                initialResponseToken = operationToken;
                return Task.FromException(initialFailure);
            },
            _ =>
            {
                reconciliationCalls++;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(1, reconciliationCalls);
        Assert.True(initialResponseToken.CanBeCanceled);
        Assert.Equal(
            InteractionInitialResponseState.Confirmed,
            fixture.ExecutionContext.InitialResponse.State);
    }

    [Fact]
    public async Task ExecuteInitialResponseAsync_PreservesSendAndReconciliationFailures()
    {
        var fixture = CreateFixture();
        using var scope = fixture.ExecutionContext.Enter(CancellationToken.None);
        var initialFailure = new InvalidOperationException("send failed");
        var reconciliationFailure = new TimeoutException("probe failed");

        var thrown = await Assert.ThrowsAsync<AggregateException>(
            () => fixture.Service.ExecuteInitialResponseAsync(
                supportsOriginalResponse: true,
                _ => Task.FromException(initialFailure),
                _ => Task.FromException(reconciliationFailure),
                CancellationToken.None));

        Assert.Equal(
            [initialFailure, reconciliationFailure],
            thrown.InnerExceptions);
        Assert.Equal(
            InteractionInitialResponseState.Attempted,
            fixture.ExecutionContext.InitialResponse.State);
    }

    [Fact]
    public void DraftOperations_PreserveOwnerScopeAndTerminalState()
    {
        var fixture = CreateFixture();

        var created = fixture.Service.CreateDraft(
            1UL,
            2UL,
            3UL,
            "Games",
            "Choose games",
            [4UL],
            RoleMenuSelectionMode.Multiple,
            out var draft);

        Assert.Equal(RoleMenuDraftCreateStatus.Created, created);
        Assert.NotNull(draft);
        Assert.Equal(
            RoleMenuDraftAccessStatus.Acquired,
            fixture.Service.TryBeginPublish(draft.Id, 1UL, 2UL, out var acquired));
        Assert.NotNull(acquired);
        Assert.Equal(draft.Id, acquired.Id);
        Assert.Equal(draft.MenuId, acquired.MenuId);

        fixture.Service.ReleasePublish(draft.Id, 1UL, 2UL);
        Assert.True(fixture.Service.CancelDraft(draft.Id, 1UL, 2UL));
        Assert.Equal(
            RoleMenuDraftAccessStatus.NotFound,
            fixture.Service.TryBeginPublish(draft.Id, 1UL, 2UL, out _));

        Assert.Equal(
            RoleMenuDraftCreateStatus.Created,
            fixture.Service.CreateDraft(
                1UL,
                2UL,
                3UL,
                "Games",
                string.Empty,
                [4UL],
                RoleMenuSelectionMode.Exclusive,
                out var completedDraft));
        Assert.NotNull(completedDraft);
        fixture.Service.CompletePublish(completedDraft.Id, 1UL, 2UL);
        Assert.Equal(
            RoleMenuDraftAccessStatus.NotFound,
            fixture.Service.TryBeginPublish(completedDraft.Id, 1UL, 2UL, out _));
    }

    [Fact]
    public async Task PersistenceOperations_ForwardGuildScopeAndBounds()
    {
        var fixture = CreateFixture();
        var settings = CreateSettings();

        await fixture.Service.UpsertAsync(settings, CancellationToken.None);

        Assert.Same(
            settings,
            await fixture.Service.GetAsync(settings.Id, 1UL, CancellationToken.None));
        Assert.Equal(
            [settings],
            await fixture.Service.GetByGuildAsync(1UL, 1, CancellationToken.None));
        Assert.True(await fixture.Service.DeleteAsync(
            settings.Id,
            1UL,
            CancellationToken.None));
        Assert.Null(await fixture.Service.GetAsync(
            settings.Id,
            1UL,
            CancellationToken.None));
    }

    [Fact]
    public async Task CoordinationOperations_ExecuteThroughTheExpectedBoundedLane()
    {
        var fixture = CreateFixture();
        var menuId = ObjectId.GenerateNewId();

        var menuResult = await fixture.Service.RunMenuMutationAsync(
            menuId,
            _ => Task.FromResult("menu"),
            CancellationToken.None);
        var memberResult = await fixture.Service.RunMemberMutationAsync(
            menuId,
            1UL,
            2UL,
            _ => Task.FromResult("member"),
            CancellationToken.None);
        Assert.Equal("menu", menuResult);
        Assert.Equal("member", memberResult);
    }

    private static ServiceFixture CreateFixture()
    {
        var store = new InMemoryStore();
        var repository = new RoleMenuRepository(
            store,
            NullLogger<RoleMenuRepository>.Instance);
        var drafts = new RoleMenuDraftRegistry();
        var coordinator = new RoleMenuMutationCoordinator();
        var executionContext = new InteractionExecutionContext();
        return new ServiceFixture(
            new RoleMenuInteractionService(
                repository,
                drafts,
                coordinator,
                executionContext),
            repository,
            drafts,
            coordinator,
            executionContext,
            store);
    }

    private static RoleMenuSettings CreateSettings()
        => new(
            ObjectId.GenerateNewId(),
            "1",
            "2",
            "3",
            "Games",
            string.Empty,
            ["4"],
            RoleMenuSelectionMode.Multiple);

    private sealed record ServiceFixture(
        RoleMenuInteractionService Service,
        RoleMenuRepository Repository,
        RoleMenuDraftRegistry Drafts,
        RoleMenuMutationCoordinator Coordinator,
        InteractionExecutionContext ExecutionContext,
        InMemoryStore Store);

    private sealed class InMemoryStore : IRoleMenuStore
    {
        private RoleMenuSettings? _settings;
        internal Action? OnGetByMessage { get; set; }

        public Task UpsertAsync(
            RoleMenuSettings settings,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _settings = settings;
            return Task.CompletedTask;
        }

        public Task<RoleMenuSettings?> GetByIdAsync(
            ObjectId id,
            string guildId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                _settings?.Id == id
                && string.Equals(_settings.GuildId, guildId, StringComparison.Ordinal)
                    ? _settings
                    : null);
        }

        public Task<List<RoleMenuSettings>> GetByGuildAsync(
            string guildId,
            int maximumResults,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<RoleMenuSettings> settings = _settings is not null
                && string.Equals(
                    _settings.GuildId,
                    guildId,
                    StringComparison.Ordinal)
                ? [_settings]
                : [];
            return Task.FromResult(settings.Take(maximumResults).ToList());
        }

        public Task<List<RoleMenuSettings>> GetByMessageAsync(
            string guildId,
            string channelId,
            string messageId,
            int maximumResults,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<RoleMenuSettings> settings = _settings is not null
                && _settings.GuildId == guildId
                && _settings.ChannelId == channelId
                && _settings.MessageId == messageId
                ? [_settings]
                : [];
            OnGetByMessage?.Invoke();
            return Task.FromResult(settings);
        }

        public Task<List<RoleMenuSettings>> GetPageAsync(
            string guildId,
            RoleMenuPageCursor? cursor,
            int maximumResults,
            CancellationToken cancellationToken)
            => GetByGuildAsync(guildId, maximumResults, cancellationToken);

        public Task<bool> DeleteAsync(
            ObjectId id,
            string guildId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deleted = _settings?.Id == id
                          && string.Equals(
                              _settings.GuildId,
                              guildId,
                              StringComparison.Ordinal);
            if (deleted)
            {
                _settings = null;
            }

            return Task.FromResult(deleted);
        }

        public Task<bool> DeleteBindingAsync(RoleMenuSettings settings, CancellationToken cancellationToken)
        {
            var current = _settings;
            if (current is null || current.Id != settings.Id || current.GuildId != settings.GuildId
                || current.ChannelId != settings.ChannelId || current.MessageId != settings.MessageId
                || current.UpdatedAtUtc != settings.UpdatedAtUtc)
            {
                return Task.FromResult(false);
            }

            _settings = null;
            return Task.FromResult(true);
        }
    }
}
