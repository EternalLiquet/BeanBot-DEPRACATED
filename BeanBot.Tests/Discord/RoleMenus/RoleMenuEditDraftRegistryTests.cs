using BeanBot.Discord.RoleMenus;
using BeanBot.Persistence.Models;
using MongoDB.Bson;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class RoleMenuEditDraftRegistryTests
{
    private static readonly ObjectId MenuId =
        ObjectId.Parse("64e7611aaac75f172f0f6789");

    [Fact]
    public void Create_ReplacesPreviousDraftForSameOwner()
    {
        var registry = CreateRegistry(capacity: 2);

        Assert.Equal(
            RoleMenuEditDraftCreateStatus.Created,
            registry.Create(
                MenuId,
                1,
                2,
                "First",
                string.Empty,
                [10],
                RoleMenuSelectionMode.Multiple,
                out var first));
        Assert.Equal(
            RoleMenuEditDraftCreateStatus.Created,
            registry.Create(
                MenuId,
                1,
                2,
                "Second",
                string.Empty,
                [11],
                RoleMenuSelectionMode.Exclusive,
                out var second));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(
            RoleMenuEditDraftAccessStatus.NotFound,
            registry.TryGet(first.Id, 1, 2, out _));
        Assert.Equal(
            RoleMenuEditDraftAccessStatus.Acquired,
            registry.TryGet(second.Id, 1, 2, out var current));
        Assert.Equal("Second", current!.Title);
    }

    [Fact]
    public void Create_WhenAtCapacity_RejectsAdditionalOwner()
    {
        var registry = CreateRegistry(capacity: 1);
        Assert.Equal(
            RoleMenuEditDraftCreateStatus.Created,
            registry.Create(
                MenuId,
                1,
                2,
                "First",
                string.Empty,
                [10],
                RoleMenuSelectionMode.Multiple,
                out _));

        Assert.Equal(
            RoleMenuEditDraftCreateStatus.CapacityReached,
            registry.Create(
                ObjectId.GenerateNewId(),
                1,
                3,
                "Second",
                string.Empty,
                [11],
                RoleMenuSelectionMode.Multiple,
                out var rejected));
        Assert.Null(rejected);
    }

    [Fact]
    public void TryGet_WrongOwnerCannotAccessDraft()
    {
        var registry = CreateRegistry();
        registry.Create(
            MenuId,
            1,
            2,
            "Menu",
            string.Empty,
            [10],
            RoleMenuSelectionMode.Multiple,
            out var draft);

        Assert.Equal(
            RoleMenuEditDraftAccessStatus.WrongOwner,
            registry.TryGet(draft!.Id, 1, 99, out var inaccessible));
        Assert.Null(inaccessible);
    }

    [Fact]
    public void TryBeginSubmit_PreventsDuplicateSubmissionAndOwnerReplacement()
    {
        var registry = CreateRegistry();
        registry.Create(
            MenuId,
            1,
            2,
            "Menu",
            string.Empty,
            [10],
            RoleMenuSelectionMode.Multiple,
            out var draft);

        Assert.Equal(
            RoleMenuEditDraftAccessStatus.Acquired,
            registry.TryBeginSubmit(draft!.Id, 1, 2, out _));
        Assert.Equal(
            RoleMenuEditDraftAccessStatus.AlreadySubmitting,
            registry.TryBeginSubmit(draft.Id, 1, 2, out _));
        Assert.Equal(
            RoleMenuEditDraftCreateStatus.AlreadySubmitting,
            registry.Create(
                ObjectId.GenerateNewId(),
                1,
                2,
                "Replacement",
                string.Empty,
                [11],
                RoleMenuSelectionMode.Multiple,
                out var replacement));
        Assert.Null(replacement);
    }

    [Fact]
    public void ExpiredDraft_IsPurgedAndCapacityBecomesAvailable()
    {
        var time = new TestTimeProvider(
            new DateTimeOffset(2026, 9, 11, 14, 0, 0, TimeSpan.Zero));
        var registry = new RoleMenuEditDraftRegistry(
            time,
            capacity: 1,
            lifetime: TimeSpan.FromMinutes(10));
        registry.Create(
            MenuId,
            1,
            2,
            "Menu",
            string.Empty,
            [10],
            RoleMenuSelectionMode.Multiple,
            out var expired);

        time.UtcNow = time.UtcNow.AddMinutes(11);

        Assert.Equal(
            RoleMenuEditDraftCreateStatus.Created,
            registry.Create(
                ObjectId.GenerateNewId(),
                1,
                3,
                "New menu",
                string.Empty,
                [11],
                RoleMenuSelectionMode.Multiple,
                out var created));
        Assert.NotNull(created);
        Assert.Equal(
            RoleMenuEditDraftAccessStatus.NotFound,
            registry.TryGet(expired!.Id, 1, 2, out _));
    }

    private static RoleMenuEditDraftRegistry CreateRegistry(int capacity = 4)
        => new(
            TimeProvider.System,
            capacity,
            TimeSpan.FromMinutes(10));

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
