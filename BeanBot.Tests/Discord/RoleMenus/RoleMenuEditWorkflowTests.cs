using BeanBot.Discord.RoleMenus;
using BeanBot.Persistence.Models;
using MongoDB.Bson;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class RoleMenuEditWorkflowTests
{
    private static readonly ObjectId MenuId =
        ObjectId.Parse("64e7611aaac75f172f0f5678");

    [Fact]
    public async Task ExecuteAsync_Success_PersistsBeforeUpdatingExistingPanel()
    {
        var calls = new List<string>();
        var replacement = CreateSettings();
        var result = await RoleMenuEditWorkflow.ExecuteAsync(
            replacement,
            new RoleMenuEditCommitOperations(
                (settings, _) =>
                {
                    Assert.Same(replacement, settings);
                    calls.Add("persist");
                    return Task.CompletedTask;
                },
                (settings, _) =>
                {
                    Assert.Same(replacement, settings);
                    calls.Add("panel");
                    return Task.FromResult(RoleMenuPanelUpdateStatus.Updated);
                },
                () => false),
            CancellationToken.None);

        Assert.Equal(RoleMenuEditCommitStatus.Updated, result.Status);
        Assert.Equal(["persist", "panel"], calls);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task ExecuteAsync_PersistenceFailure_DoesNotTouchPanel()
    {
        var expected = new InvalidOperationException("mongo outcome unknown");
        var panelCalls = 0;
        var result = await RoleMenuEditWorkflow.ExecuteAsync(
            CreateSettings(),
            new RoleMenuEditCommitOperations(
                (_, _) => Task.FromException(expected),
                (_, _) =>
                {
                    panelCalls++;
                    return Task.FromResult(RoleMenuPanelUpdateStatus.Updated);
                },
                () => false),
            CancellationToken.None);

        Assert.Equal(RoleMenuEditCommitStatus.PersistenceOutcomeUnknown, result.Status);
        Assert.Equal(0, panelCalls);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(RoleMenuEditFailurePhase.Persistence, failure.Phase);
        Assert.Same(expected, failure.Exception);
    }

    [Theory]
    [InlineData(RoleMenuPanelUpdateStatus.Missing, RoleMenuEditCommitStatus.PanelMissing)]
    [InlineData(RoleMenuPanelUpdateStatus.UnexpectedMessage, RoleMenuEditCommitStatus.PanelUnexpected)]
    public async Task ExecuteAsync_DefinitePanelFailure_KeepsPersistedEdit(
        RoleMenuPanelUpdateStatus panelStatus,
        RoleMenuEditCommitStatus expectedStatus)
    {
        var persistCalls = 0;
        var result = await RoleMenuEditWorkflow.ExecuteAsync(
            CreateSettings(),
            new RoleMenuEditCommitOperations(
                (_, _) =>
                {
                    persistCalls++;
                    return Task.CompletedTask;
                },
                (_, _) => Task.FromResult(panelStatus),
                () => false),
            CancellationToken.None);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(1, persistCalls);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task ExecuteAsync_AmbiguousPanelFailure_DoesNotRetryOrRollbackPersistence()
    {
        var expected = new TimeoutException("discord outcome unknown");
        var persistCalls = 0;
        var panelCalls = 0;
        var result = await RoleMenuEditWorkflow.ExecuteAsync(
            CreateSettings(),
            new RoleMenuEditCommitOperations(
                (_, _) =>
                {
                    persistCalls++;
                    return Task.CompletedTask;
                },
                (_, _) =>
                {
                    panelCalls++;
                    return Task.FromException<RoleMenuPanelUpdateStatus>(expected);
                },
                () => false),
            CancellationToken.None);

        Assert.Equal(RoleMenuEditCommitStatus.PanelOutcomeUnknown, result.Status);
        Assert.Equal(1, persistCalls);
        Assert.Equal(1, panelCalls);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(RoleMenuEditFailurePhase.PanelUpdate, failure.Phase);
        Assert.Same(expected, failure.Exception);
    }

    [Fact]
    public async Task ExecuteAsync_RepeatedSuccessfulEdit_IsIdempotentAtWorkflowBoundary()
    {
        var persistCalls = 0;
        var panelCalls = 0;
        var operations = new RoleMenuEditCommitOperations(
            (_, _) =>
            {
                persistCalls++;
                return Task.CompletedTask;
            },
            (_, _) =>
            {
                panelCalls++;
                return Task.FromResult(RoleMenuPanelUpdateStatus.Updated);
            },
            () => false);
        var replacement = CreateSettings();

        var first = await RoleMenuEditWorkflow.ExecuteAsync(
            replacement,
            operations,
            CancellationToken.None);
        var second = await RoleMenuEditWorkflow.ExecuteAsync(
            replacement,
            operations,
            CancellationToken.None);

        Assert.Equal(RoleMenuEditCommitStatus.Updated, first.Status);
        Assert.Equal(RoleMenuEditCommitStatus.Updated, second.Status);
        Assert.Equal(2, persistCalls);
        Assert.Equal(2, panelCalls);
    }

    [Fact]
    public async Task ExecuteAsync_ShutdownCancellation_PropagatesWithoutPanelWork()
    {
        var panelCalls = 0;
        var cancellation = new OperationCanceledException("shutdown");

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            RoleMenuEditWorkflow.ExecuteAsync(
                CreateSettings(),
                new RoleMenuEditCommitOperations(
                    (_, _) => Task.FromException(cancellation),
                    (_, _) =>
                    {
                        panelCalls++;
                        return Task.FromResult(RoleMenuPanelUpdateStatus.Updated);
                    },
                    () => true),
                CancellationToken.None));

        Assert.Equal(0, panelCalls);
    }

    private static RoleMenuSettings CreateSettings()
        => new(
            MenuId,
            "101",
            "202",
            "303",
            "Edited roles",
            "Updated description",
            ["404", "405"],
            RoleMenuSelectionMode.Multiple)
        {
            CreatedAtUtc = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)
        };
}
