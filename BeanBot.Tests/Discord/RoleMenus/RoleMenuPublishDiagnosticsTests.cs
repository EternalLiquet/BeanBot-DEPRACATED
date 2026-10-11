using BeanBot.Discord.RoleMenus;
using BeanBot.Persistence.Models;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class RoleMenuPublishDiagnosticsTests
{
    [Fact]
    public async Task SuccessfulPhasesShareAttemptAndReportElapsedTime()
    {
        var logger = new RecordingLogger();
        var diagnostics = new RoleMenuPublishDiagnostics(logger);

        await diagnostics.TrackAsync("mongo_read", () => Task.FromResult(1));
        await diagnostics.TrackAsync("history_lookup", () => Task.FromResult(2));
        diagnostics.Finish("published");

        Assert.Equal(6, logger.Entries.Count);
        Assert.Equal(["started", "started", "completed", "started", "completed", "published"],
            logger.Entries.Select(entry => (string)entry.Properties["Outcome"]!));
        Assert.Single(logger.Entries.Select(entry => entry.Properties["AttemptId"]).Distinct());
        Assert.All(logger.Entries, entry =>
            Assert.True(entry.Properties["ElapsedMs"] is double elapsed && elapsed >= 0));
    }

    [Fact]
    public async Task CancellationReportsActivePhaseWithoutSensitiveValues()
    {
        var logger = new RecordingLogger();
        var diagnostics = new RoleMenuPublishDiagnostics(logger);
        const string secret = "secret-menu-name-and-payload";
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            diagnostics.TrackAsync("history_lookup", () => Task.FromCanceled<int>(cancellation.Token)));
        diagnostics.Finish("timeout");

        Assert.Equal("history_lookup", logger.Entries[^1].Properties["Phase"]);
        Assert.Equal("timeout", logger.Entries[^1].Properties["Outcome"]);
        Assert.All(logger.Entries, entry =>
        {
            Assert.True(entry.Level < LogLevel.Error);
            Assert.DoesNotContain(secret, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("MenuId", entry.Properties.Keys);
        });
    }

    [Fact]
    public async Task ConcurrentAttemptsKeepSeparateCorrelationIds()
    {
        var logger = new RecordingLogger();
        var first = new RoleMenuPublishDiagnostics(logger);
        var second = new RoleMenuPublishDiagnostics(logger);
        await Task.WhenAll(
            first.TrackAsync("mongo_read", () => Task.FromResult(1)),
            second.TrackAsync("mongo_read", () => Task.FromResult(2)));
        first.Finish("published");
        second.Finish("published");

        Assert.Equal(2, logger.Entries.Select(entry => entry.Properties["AttemptId"]).Distinct().Count());
    }

    [Theory]
    [InlineData("mongo_read")]
    [InlineData("history_lookup")]
    public async Task WorkflowStallIdentifiesActivePreSendPhase(string stalledPhase)
    {
        var logger = new RecordingLogger();
        var diagnostics = new RoleMenuPublishDiagnostics(logger);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operations = CreateOperations(
            readSettings: (_, _, _) => stalledPhase == "mongo_read"
                ? Stall<RoleMenuSettings?>(entered, release, null)
                : Task.FromResult<RoleMenuSettings?>(null),
            readRecent: (_, _, _) => stalledPhase == "history_lookup"
                ? Stall<IReadOnlyList<RoleMenuPanelSnapshot>>(
                    entered, release, [])
                : Task.FromResult<IReadOnlyList<RoleMenuPanelSnapshot>>([]));

        var publish = RoleMenuPublicationWorkflow.ExecuteAsync(
            CreateDraft(), 90UL, operations, CancellationToken.None, diagnostics);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        diagnostics.Finish("timeout");
        Assert.Equal(stalledPhase, logger.Entries[^1].Properties["Phase"]);
        Assert.Equal("timeout", logger.Entries[^1].Properties["Outcome"]);
        release.TrySetResult();
        await publish.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData("discord_send", "panel_reconciliation_history")]
    [InlineData("persistence", "persistence_reconciliation_read")]
    public async Task WorkflowFailuresReportFailedPhaseAndReconciliation(
        string failingPhase, string reconciliationPhase)
    {
        var logger = new RecordingLogger();
        var diagnostics = new RoleMenuPublishDiagnostics(logger);
        var operations = CreateOperations(
            send: (_, _) => failingPhase == "discord_send"
                ? Task.FromException<RoleMenuPanelSnapshot>(new InvalidOperationException("secret payload"))
                : Task.FromResult(new RoleMenuPanelSnapshot(1, 2, 100, 90, true)),
            upsert: (_, _) => failingPhase == "persistence"
                ? Task.FromException(new InvalidOperationException("secret payload"))
                : Task.CompletedTask);

        await RoleMenuPublicationWorkflow.ExecuteAsync(
            CreateDraft(), 90UL, operations, CancellationToken.None, diagnostics);

        Assert.Contains(logger.Entries, entry =>
            Equals(entry.Properties["Phase"], failingPhase)
            && Equals(entry.Properties["Outcome"], "failed"));
        Assert.Contains(logger.Entries, entry =>
            Equals(entry.Properties["Phase"], reconciliationPhase)
            && Equals(entry.Properties["Outcome"], "started"));
        Assert.All(logger.Entries, entry => Assert.DoesNotContain("secret payload", entry.Message));
    }

    [Theory]
    [InlineData("panel_reconciliation_history")]
    [InlineData("persistence_reconciliation_read")]
    public async Task ReconciliationFailureReportsItsOwnPhase(string phase)
    {
        var logger = new RecordingLogger();
        var diagnostics = new RoleMenuPublishDiagnostics(logger);
        var settingsReads = 0;
        var historyReads = 0;
        var operations = CreateOperations(
            readSettings: (_, _, _) =>
            {
                settingsReads++;
                return phase == "persistence_reconciliation_read" && settingsReads == 2
                    ? Task.FromException<RoleMenuSettings?>(new InvalidOperationException("secret payload"))
                    : Task.FromResult<RoleMenuSettings?>(null);
            },
            readRecent: (_, _, _) =>
            {
                historyReads++;
                return phase == "panel_reconciliation_history" && historyReads == 2
                    ? Task.FromException<IReadOnlyList<RoleMenuPanelSnapshot>>(
                        new InvalidOperationException("secret payload"))
                    : Task.FromResult<IReadOnlyList<RoleMenuPanelSnapshot>>([]);
            },
            send: (_, _) => phase == "panel_reconciliation_history"
                ? Task.FromException<RoleMenuPanelSnapshot>(new InvalidOperationException("secret payload"))
                : Task.FromResult(new RoleMenuPanelSnapshot(1, 2, 100, 90, true)),
            upsert: (_, _) => phase == "persistence_reconciliation_read"
                ? Task.FromException(new InvalidOperationException("secret payload"))
                : Task.CompletedTask);

        await RoleMenuPublicationWorkflow.ExecuteAsync(
            CreateDraft(), 90UL, operations, CancellationToken.None, diagnostics);

        Assert.Contains(logger.Entries, entry =>
            Equals(entry.Properties["Phase"], phase)
            && Equals(entry.Properties["Outcome"], "failed"));
        Assert.All(logger.Entries, entry => Assert.DoesNotContain("secret payload", entry.Message));
    }

    private static async Task<T> Stall<T>(TaskCompletionSource entered, TaskCompletionSource release, T result)
    {
        entered.TrySetResult();
        await release.Task;
        return result;
    }

    private static RoleMenuDraft CreateDraft()
        => new(Guid.NewGuid(), ObjectId.GenerateNewId(), 1UL, 10UL, 2UL,
            "secret menu", "secret payload", [4UL], RoleMenuSelectionMode.Multiple,
            DateTimeOffset.UtcNow.AddMinutes(10));

    private static RoleMenuPublicationOperations CreateOperations(
        Func<ObjectId, ulong, CancellationToken, Task<RoleMenuSettings?>>? readSettings = null,
        Func<ulong, int, CancellationToken, Task<IReadOnlyList<RoleMenuPanelSnapshot>>>? readRecent = null,
        Func<RoleMenuDraft, CancellationToken, Task<RoleMenuPanelSnapshot>>? send = null,
        Func<RoleMenuSettings, CancellationToken, Task>? upsert = null)
        => new(
            readSettings ?? ((_, _, _) => Task.FromResult<RoleMenuSettings?>(null)),
            (_, _, _) => Task.FromResult<RoleMenuPanelSnapshot?>(null),
            readRecent ?? ((_, _, _) => Task.FromResult<IReadOnlyList<RoleMenuPanelSnapshot>>([])),
            send ?? ((_, _) => Task.FromResult(new RoleMenuPanelSnapshot(1, 2, 100, 90, true))),
            upsert ?? ((_, _) => Task.CompletedTask),
            (_, _) => Task.FromResult(true));

    private sealed record Entry(LogLevel Level, string Message, Dictionary<string, object?> Properties);

    private sealed class RecordingLogger : ILogger
    {
        internal List<Entry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(pair => pair.Key, pair => pair.Value)
                : [];
            lock (Entries)
            {
                Entries.Add(new Entry(logLevel, formatter(state, exception), properties));
            }
        }

        private sealed class NullScope : IDisposable
        {
            internal static NullScope Instance { get; } = new();
            public void Dispose() { }
        }
    }
}
