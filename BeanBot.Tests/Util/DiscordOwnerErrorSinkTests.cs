using BeanBot.Util;
using Serilog.Events;
using Serilog.Parsing;
using System.Collections.Concurrent;
using System.Diagnostics;
using Xunit;

namespace BeanBot.Tests.Util;

public class DiscordOwnerErrorSinkTests
{
    [Fact]
    public void FormatAlert_IncludesRenderedMessageAndException()
    {
        var logEvent = CreateLogEvent(LogEventLevel.Error, "Something broke", new InvalidOperationException("very broken"));

        var alert = DiscordOwnerErrorSink.FormatAlert(logEvent);

        Assert.Contains("Something broke", alert);
        Assert.Contains("InvalidOperationException", alert);
        Assert.Contains("very broken", alert);
    }

    [Fact]
    public void FormatAlert_StaysWithinDiscordMessageLimit()
    {
        var logEvent = CreateLogEvent(LogEventLevel.Error, new string('x', 3000));

        var alert = DiscordOwnerErrorSink.FormatAlert(logEvent);

        Assert.True(alert.Length <= 1900);
        Assert.EndsWith("...(truncated)", alert);
    }

    [Theory]
    [InlineData(LogEventLevel.Verbose, false)]
    [InlineData(LogEventLevel.Debug, false)]
    [InlineData(LogEventLevel.Information, false)]
    [InlineData(LogEventLevel.Warning, false)]
    [InlineData(LogEventLevel.Error, true)]
    [InlineData(LogEventLevel.Fatal, true)]
    public void Emit_OnlyEnqueuesErrorAndFatalEvents(LogEventLevel level, bool shouldEnqueue)
    {
        var notifier = new CapturingNotifier();
        var sink = new DiscordOwnerErrorSink(notifier);

        sink.Emit(CreateLogEvent(level, "event"));

        Assert.Equal(shouldEnqueue ? 1 : 0, notifier.Alerts.Count);
    }

    [Fact]
    public void Emit_WarningWithException_DoesNotEnqueue()
    {
        var notifier = new CapturingNotifier();
        var sink = new DiscordOwnerErrorSink(notifier);

        sink.Emit(CreateLogEvent(LogEventLevel.Warning, "handled", new InvalidOperationException()));

        Assert.Empty(notifier.Alerts);
    }

    [Fact]
    public void Emit_ErrorWithException_Enqueues()
    {
        var notifier = new CapturingNotifier();
        var sink = new DiscordOwnerErrorSink(notifier);

        sink.Emit(CreateLogEvent(LogEventLevel.Error, "failed", new InvalidOperationException()));

        Assert.Single(notifier.Alerts);
    }

    [Fact]
    public async Task Notifier_DropsPoisonAlertAfterBoundedRetriesAndDeliversNextAlert()
    {
        var delivery = new ScriptedDelivery(alert => alert == "poison");
        await using var notifier = new DiscordOwnerErrorNotifier(
            delivery,
            _ => TimeSpan.Zero,
            TimeSpan.FromMilliseconds(100));

        notifier.Enqueue("poison");
        notifier.Enqueue("later");
        await delivery.LaterAlertDelivered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(DiscordOwnerErrorNotifier.DefaultMaximumAttempts, delivery.Attempts["poison"]);
        Assert.Equal(1, delivery.Attempts["later"]);
        Assert.Contains("later", delivery.DeliveredAlerts);
        Assert.Equal(2, delivery.Attempts.Keys.Count);
    }

    [Fact]
    public async Task Notifier_ShutdownCancelsRetryDelayPromptly()
    {
        var delivery = new ScriptedDelivery(_ => true);
        var notifier = new DiscordOwnerErrorNotifier(
            delivery,
            _ => TimeSpan.FromMinutes(1),
            TimeSpan.FromMilliseconds(25));
        notifier.Enqueue("poison");
        await delivery.FirstAttempted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var stopwatch = Stopwatch.StartNew();

        await notifier.DisposeAsync();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
    }

    private static LogEvent CreateLogEvent(
        LogEventLevel level,
        string message,
        Exception? exception = null)
        => new LogEvent(
            DateTimeOffset.UtcNow,
            level,
            exception,
            new MessageTemplateParser().Parse(message),
            Array.Empty<LogEventProperty>());

    private sealed class CapturingNotifier : IOwnerErrorNotifier
    {
        public List<string> Alerts { get; } = new();
        public void Enqueue(string alert) => Alerts.Add(alert);
    }

    private sealed class ScriptedDelivery : IOwnerAlertDelivery
    {
        private readonly Func<string, bool> _shouldFail;

        public ScriptedDelivery(Func<string, bool> shouldFail)
        {
            _shouldFail = shouldFail;
        }

        public ConcurrentDictionary<string, int> Attempts { get; } = new();
        public ConcurrentBag<string> DeliveredAlerts { get; } = new();
        public TaskCompletionSource FirstAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LaterAlertDelivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task DeliverAsync(string alert, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts.AddOrUpdate(alert, 1, (_, count) => count + 1);
            FirstAttempted.TrySetResult();
            if (_shouldFail(alert))
            {
                throw new InvalidOperationException("delivery failed");
            }

            DeliveredAlerts.Add(alert);
            if (alert == "later")
            {
                LaterAlertDelivered.TrySetResult();
            }

            return Task.CompletedTask;
        }
    }
}
