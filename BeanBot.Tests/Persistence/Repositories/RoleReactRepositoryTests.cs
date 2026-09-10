using BeanBot.Persistence.Models;
using BeanBot.Persistence.Repositories;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace BeanBot.Tests.Persistence.Repositories;

public class RoleReactRepositoryTests
{
    [Fact]
    public async Task GetRoleSetting_ReturnsMatchingSetting()
    {
        var expected = CreateRoleSettings("42");
        var store = new FakeRoleSettingsStore
        {
            GetByMessageId = (messageId, _) =>
            {
                Assert.Equal("42", messageId);
                return Task.FromResult<RoleSettings?>(expected);
            }
        };
        var repository = CreateRepository(store);

        var actual = await repository.GetRoleSetting(42UL);

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task GetRoleSetting_ReturnsNullWhenSettingDoesNotExist()
    {
        var repository = CreateRepository(new FakeRoleSettingsStore());

        var actual = await repository.GetRoleSetting(42UL);

        Assert.Null(actual);
    }

    [Fact]
    public async Task GetRoleSetting_PropagatesInfrastructureFailure()
    {
        var expected = new InvalidOperationException("database unavailable");
        var store = new FakeRoleSettingsStore
        {
            GetByMessageId = (_, _) => Task.FromException<RoleSettings?>(expected)
        };
        var repository = CreateRepository(store);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.GetRoleSetting(42UL));

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task GetRecentRoleSettings_PassesCancellationTokenToStore()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new FakeRoleSettingsStore
        {
            GetRecent = async (_, _, cancellationToken) =>
            {
                Assert.Equal(cancellation.Token, cancellationToken);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return [];
            }
        };
        var repository = CreateRepository(store);
        var read = repository.GetRecentRoleSettings(10, cancellation.Token);
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => read);

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task GetRecentRoleSettings_UsesThirtyDayUtcCutoffAndRequestedLimit()
    {
        DateTime? observedCutoff = null;
        int? observedLimit = null;
        var beforeRead = DateTime.UtcNow.AddDays(-30);
        var store = new FakeRoleSettingsStore
        {
            GetRecent = (oldestLastAccessedUtc, limit, _) =>
            {
                observedCutoff = oldestLastAccessedUtc;
                observedLimit = limit;
                return Task.FromResult(new List<RoleSettings>());
            }
        };
        var repository = CreateRepository(store);

        await repository.GetRecentRoleSettings(17);
        var afterRead = DateTime.UtcNow.AddDays(-30);

        var cutoff = Assert.IsType<DateTime>(observedCutoff);
        Assert.Equal(DateTimeKind.Utc, cutoff.Kind);
        Assert.InRange(cutoff, beforeRead, afterRead);
        Assert.Equal(17, observedLimit);
    }

    [Fact]
    public async Task GetRecentRoleSettings_RejectsNonPositiveLimit()
    {
        var repository = CreateRepository(new FakeRoleSettingsStore());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repository.GetRecentRoleSettings(0));
    }

    [Fact]
    public async Task InsertNewRoleSettings_UpdatesLastAccessedAndPassesCancellationToken()
    {
        using var cancellation = new CancellationTokenSource();
        var settings = CreateRoleSettings("42");
        var beforeInsert = DateTime.UtcNow;
        var store = new FakeRoleSettingsStore
        {
            Insert = (actual, cancellationToken) =>
            {
                Assert.Same(settings, actual);
                Assert.Equal(cancellation.Token, cancellationToken);
                return Task.CompletedTask;
            }
        };
        var repository = CreateRepository(store);

        await repository.InsertNewRoleSettings(settings, cancellation.Token);

        Assert.Equal(DateTimeKind.Utc, settings.LastAccessedUtc.Kind);
        Assert.InRange(settings.LastAccessedUtc, beforeInsert, DateTime.UtcNow);
    }

    [Fact]
    public async Task InsertNewRoleSettings_PropagatesInfrastructureFailure()
    {
        var expected = new InvalidOperationException("database unavailable");
        var store = new FakeRoleSettingsStore
        {
            Insert = (_, _) => Task.FromException(expected)
        };
        var repository = CreateRepository(store);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.InsertNewRoleSettings(CreateRoleSettings("42")));

        Assert.Same(expected, actual);
    }

    private static RoleReactRepository CreateRepository(IRoleSettingsStore store)
        => new(store, NullLogger<RoleReactRepository>.Instance);

    private static RoleSettings CreateRoleSettings(string messageId)
        => new([], "1", "2", messageId);

    private sealed class FakeRoleSettingsStore : IRoleSettingsStore
    {
        public Func<RoleSettings, CancellationToken, Task> Insert { get; set; }
            = (_, _) => Task.CompletedTask;
        public Func<DateTime, int, CancellationToken, Task<List<RoleSettings>>> GetRecent { get; set; }
            = (_, _, _) => Task.FromResult(new List<RoleSettings>());
        public Func<string, CancellationToken, Task<RoleSettings?>> GetByMessageId { get; set; }
            = (_, _) => Task.FromResult<RoleSettings?>(null);

        public Task InsertAsync(RoleSettings roleSettings, CancellationToken cancellationToken)
            => Insert(roleSettings, cancellationToken);

        public Task<List<RoleSettings>> GetRecentAsync(
            DateTime oldestLastAccessedUtc,
            int limit,
            CancellationToken cancellationToken)
            => GetRecent(oldestLastAccessedUtc, limit, cancellationToken);

        public Task<RoleSettings?> GetByMessageIdAsync(
            string messageId,
            CancellationToken cancellationToken)
            => GetByMessageId(messageId, cancellationToken);
    }
}
