using BeanBot.Discord.RoleMenus;
using BeanBot.Persistence.Models;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class RoleMenuDraftRegistryTests
{
    [Fact]
    public void Create_ReplacesPriorDraftForSameGuildAndOwner()
    {
        var registry = CreateRegistry(capacity: 1);

        Assert.Equal(RoleMenuDraftCreateStatus.Created, Create(registry, out var first));
        Assert.Equal(RoleMenuDraftCreateStatus.Created, Create(registry, out var second));

        var firstDraft = Assert.IsType<RoleMenuDraft>(first);
        var secondDraft = Assert.IsType<RoleMenuDraft>(second);
        Assert.NotEqual(firstDraft.Id, secondDraft.Id);
        Assert.Equal(
            RoleMenuDraftAccessStatus.NotFound,
            registry.TryBeginPublish(firstDraft.Id, 1, 2, out _));
        Assert.Equal(
            RoleMenuDraftAccessStatus.Acquired,
            registry.TryBeginPublish(secondDraft.Id, 1, 2, out _));
    }

    [Fact]
    public void Create_EnforcesGlobalBoundAcrossOwners()
    {
        var registry = CreateRegistry(capacity: 1);
        Assert.Equal(RoleMenuDraftCreateStatus.Created, Create(registry, out _));

        var status = registry.Create(
            9,
            9,
            9,
            "Other",
            string.Empty,
            [10UL],
            RoleMenuSelectionMode.Multiple,
            out var draft);

        Assert.Equal(RoleMenuDraftCreateStatus.CapacityReached, status);
        Assert.Null(draft);
    }

    [Fact]
    public void TryBeginPublish_EnforcesGuildOwnerAndSinglePublisher()
    {
        var registry = CreateRegistry();
        Create(registry, out var draft);
        var createdDraft = Assert.IsType<RoleMenuDraft>(draft);

        Assert.Equal(
            RoleMenuDraftAccessStatus.WrongOwner,
            registry.TryBeginPublish(createdDraft.Id, 1, 99, out _));
        Assert.Equal(
            RoleMenuDraftAccessStatus.WrongOwner,
            registry.TryBeginPublish(createdDraft.Id, 99, 2, out _));
        Assert.Equal(
            RoleMenuDraftAccessStatus.Acquired,
            registry.TryBeginPublish(createdDraft.Id, 1, 2, out _));
        Assert.Equal(
            RoleMenuDraftAccessStatus.AlreadyPublishing,
            registry.TryBeginPublish(createdDraft.Id, 1, 2, out _));

        registry.ReleasePublish(createdDraft.Id, 1, 2);
        Assert.Equal(
            RoleMenuDraftAccessStatus.Acquired,
            registry.TryBeginPublish(createdDraft.Id, 1, 2, out _));
    }

    [Fact]
    public void ExpiredDraft_IsPurgedAndCapacityCanBeReused()
    {
        var clock = new MutableTimeProvider(
            new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero));
        var registry = new RoleMenuDraftRegistry(
            clock,
            1,
            TimeSpan.FromMinutes(10));
        Create(registry, out var expired);
        var expiredDraft = Assert.IsType<RoleMenuDraft>(expired);

        clock.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(
            RoleMenuDraftAccessStatus.NotFound,
            registry.TryBeginPublish(expiredDraft.Id, 1, 2, out _));
        Assert.Equal(RoleMenuDraftCreateStatus.Created, Create(registry, out _));
    }

    [Fact]
    public void CompletePublishAndCancelRemoveDraft()
    {
        var registry = CreateRegistry();
        Create(registry, out var published);
        var publishedDraft = Assert.IsType<RoleMenuDraft>(published);
        registry.TryBeginPublish(publishedDraft.Id, 1, 2, out _);

        registry.CompletePublish(publishedDraft.Id, 1, 2);

        Assert.Equal(
            RoleMenuDraftAccessStatus.NotFound,
            registry.TryBeginPublish(publishedDraft.Id, 1, 2, out _));

        Create(registry, out var cancelled);
        var cancelledDraft = Assert.IsType<RoleMenuDraft>(cancelled);
        Assert.True(registry.Cancel(cancelledDraft.Id, 1, 2));
        Assert.False(registry.Cancel(cancelledDraft.Id, 1, 2));
    }

    [Fact]
    public void Create_DoesNotReplaceDraftWhileItIsPublishing()
    {
        var registry = CreateRegistry();
        Create(registry, out var publishing);
        var publishingDraft = Assert.IsType<RoleMenuDraft>(publishing);
        registry.TryBeginPublish(publishingDraft.Id, 1, 2, out _);

        var status = Create(registry, out var replacement);

        Assert.Equal(RoleMenuDraftCreateStatus.AlreadyPublishing, status);
        Assert.Null(replacement);
        Assert.Equal(
            RoleMenuDraftAccessStatus.AlreadyPublishing,
            registry.TryBeginPublish(publishingDraft.Id, 1, 2, out _));
    }

    [Fact]
    public void PublishingDraft_DoesNotExpireAndReleaseRefreshesItsLifetime()
    {
        var clock = new MutableTimeProvider(
            new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero));
        var registry = new RoleMenuDraftRegistry(
            clock,
            2,
            TimeSpan.FromMinutes(10));
        Create(registry, out var publishing);
        var publishingDraft = Assert.IsType<RoleMenuDraft>(publishing);
        registry.TryBeginPublish(publishingDraft.Id, 1, 2, out _);
        clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal(RoleMenuDraftCreateStatus.Created, registry.Create(
            9,
            9,
            9,
            "Other",
            string.Empty,
            [10UL],
            RoleMenuSelectionMode.Multiple,
            out _));
        Assert.Equal(
            RoleMenuDraftAccessStatus.AlreadyPublishing,
            registry.TryBeginPublish(publishingDraft.Id, 1, 2, out _));

        registry.ReleasePublish(publishingDraft.Id, 1, 2);
        Assert.Equal(
            RoleMenuDraftAccessStatus.Acquired,
            registry.TryBeginPublish(publishingDraft.Id, 1, 2, out _));
    }

    [Fact]
    public void ReleaseAndRetry_PreserveStablePublicationMenuId()
    {
        var registry = CreateRegistry();
        Create(registry, out var created);
        var draft = Assert.IsType<RoleMenuDraft>(created);
        Assert.Equal(
            RoleMenuDraftAccessStatus.Acquired,
            registry.TryBeginPublish(draft.Id, 1, 2, out var firstAttempt));
        registry.ReleasePublish(draft.Id, 1, 2);

        Assert.Equal(
            RoleMenuDraftAccessStatus.Acquired,
            registry.TryBeginPublish(draft.Id, 1, 2, out var retryAttempt));

        Assert.Equal(
            Assert.IsType<RoleMenuDraft>(firstAttempt).MenuId,
            Assert.IsType<RoleMenuDraft>(retryAttempt).MenuId);
    }

    private static RoleMenuDraftRegistry CreateRegistry(int capacity = 4)
        => new(
            TimeProvider.System,
            capacity,
            TimeSpan.FromMinutes(10));

    private static RoleMenuDraftCreateStatus Create(
        RoleMenuDraftRegistry registry,
        out RoleMenuDraft? draft)
        => registry.Create(
            1,
            2,
            3,
            "Games",
            string.Empty,
            [4UL],
            RoleMenuSelectionMode.Multiple,
            out draft);

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
