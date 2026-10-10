using System.Collections.Concurrent;
using System.Diagnostics;
using BeanBot.Hosting;
using BeanBot.Logging;
using Xunit;

namespace BeanBot.Tests.Hosting;

public class BeanBotStartupReportTests
{
    [Fact]
    public void Format_UsesRunningVersionAndReadableChannelNameWithoutUrlsOrIds()
    {
        var report = BeanBotStartupReport.Format(
            "2.18.3",
            PunChannelStartupStatus.FromCachedChannel("daily-puns", canView: true, canSend: true),
            new Uri("https://user:password@example.test/image?token=private"),
            new Uri("https://example.test/secret-path?key=secret"));

        Assert.Contains("Bean Bot v2.18.3 started.", report);
        Assert.Contains("Daily pun channel: #daily-puns — Ready (View Channel and Send Messages available).", report);
        Assert.Contains("Hatoete image URL: configured (reachability not checked).", report);
        Assert.Contains("Yoshimaru image URL: configured (reachability not checked).", report);
        Assert.DoesNotContain("example.test", report);
        Assert.DoesNotContain("password", report);
        Assert.DoesNotContain("token", report);
        Assert.DoesNotContain("secret", report);
    }

    [Theory]
    [InlineData(false, true, "View Channel")]
    [InlineData(true, false, "Send Messages")]
    [InlineData(false, false, "View Channel and Send Messages")]
    public void Format_NamesMissingPostingPermissions(
        bool canView,
        bool canSend,
        string missingPermissions)
    {
        var report = BeanBotStartupReport.Format(
            "2.18.3",
            PunChannelStartupStatus.FromCachedChannel("daily-puns", canView, canSend),
            null,
            null);

        Assert.Contains($"#daily-puns — missing permissions: {missingPermissions}.", report);
    }

    [Fact]
    public void Format_DistinguishesMissingChannelAndMissingUrls()
    {
        var report = BeanBotStartupReport.Format(
            "2.18.3",
            PunChannelStartupStatus.NotFound,
            null,
            null);

        Assert.Contains("Daily pun channel: not found. Check the channel setting and my access.", report);
        Assert.Contains("Hatoete image URL: missing.", report);
        Assert.Contains("Yoshimaru image URL: missing.", report);
    }

    [Fact]
    public void Format_DoesNotClaimPermissionWhenBotIsUnavailableInCache()
    {
        var report = BeanBotStartupReport.Format(
            "2.18.3",
            PunChannelStartupStatus.FromCachedChannel("daily-puns", null, null),
            null,
            null);

        Assert.Contains("#daily-puns — I couldn't check this channel yet.", report);
        Assert.DoesNotContain("Ready", report);
    }

    [Theory]
    [InlineData("0.0.0-local", "Bean Bot development build started.")]
    [InlineData("unknown", "Bean Bot started (version unavailable).")]
    public void Format_UsesHonestVersionFallback(string version, string expected)
    {
        var report = BeanBotStartupReport.Format(
            version,
            PunChannelStartupStatus.NotFound,
            null,
            null);

        Assert.StartsWith(expected, report);
    }

    [Fact]
    public void QueueOnFirstReady_IsAtomicAndDoesNotRepeatOnReconnect()
    {
        var notifier = new CapturingNotifier();
        var checks = 0;
        var recorded = new ConcurrentQueue<bool>();
        var reporter = new BeanBotStartupReport(
            () =>
            {
                Interlocked.Increment(ref checks);
                return PunChannelStartupStatus.FromCachedChannel("daily-puns", true, true);
            },
            "2.18.3",
            null,
            null,
            recorded.Enqueue,
            notifier);

        Parallel.For(0, 20, _ => reporter.QueueOnFirstReady());
        reporter.QueueOnFirstReady();

        Assert.Single(notifier.Alerts);
        Assert.Equal(1, checks);
        Assert.Equal([false], recorded);
    }

    [Fact]
    public void QueueOnFirstReady_MissingChannelConsumesScheduledAlertBeforeQueueing()
    {
        var notifier = new CapturingNotifier();
        var recorded = new ConcurrentQueue<string>();
        var reporter = new BeanBotStartupReport(
            () => PunChannelStartupStatus.NotFound,
            "2.18.3",
            null,
            null,
            unhealthy => recorded.Enqueue($"mark:{unhealthy}"),
            new RecordingOrderNotifier(recorded, notifier));

        reporter.QueueOnFirstReady();

        Assert.Equal(["mark:True", "enqueue"], recorded);
        Assert.Single(notifier.Alerts);
    }

    [Fact]
    public void QueueOnFirstReady_ChannelCheckFailureStillSendsHonestReport()
    {
        var notifier = new CapturingNotifier();
        var reporter = new BeanBotStartupReport(
            () => throw new InvalidOperationException("cache unavailable"),
            "2.18.3",
            null,
            null,
            _ => { },
            notifier);

        reporter.QueueOnFirstReady();

        Assert.Contains("I couldn't check this channel yet", Assert.Single(notifier.Alerts));
    }

    [Fact]
    public void Format_SanitizesUntrustedChannelName()
    {
        var report = BeanBotStartupReport.Format(
            "2.18.3",
            PunChannelStartupStatus.FromCachedChannel("daily\n@everyone`puns", true, true),
            null,
            null);

        Assert.DoesNotContain("@everyone", report);
        Assert.DoesNotContain('`', report);
        Assert.DoesNotContain("daily\n", report);
    }

    [Fact]
    public async Task QueueOnFirstReady_FailedDeliveryDoesNotBlockOrRepeatOnReconnect()
    {
        var delivery = new FailingDelivery();
        await using var notifier = new DiscordOwnerErrorNotifier(
            delivery,
            _ => TimeSpan.Zero,
            TimeSpan.FromMilliseconds(100));
        var reporter = new BeanBotStartupReport(
            () => PunChannelStartupStatus.NotFound,
            "2.18.3",
            null,
            null,
            _ => { },
            notifier);

        var stopwatch = Stopwatch.StartNew();
        reporter.QueueOnFirstReady();
        reporter.QueueOnFirstReady();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        await notifier.FlushAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(DiscordOwnerErrorNotifier.DefaultMaximumAttempts, delivery.Attempts);
        await notifier.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
    }

    private sealed class CapturingNotifier : IOwnerErrorNotifier
    {
        public ConcurrentQueue<string> Alerts { get; } = new();
        public void Enqueue(string alert) => Alerts.Enqueue(alert);
    }

    private sealed class RecordingOrderNotifier(
        ConcurrentQueue<string> recorded,
        IOwnerErrorNotifier inner) : IOwnerErrorNotifier
    {
        public void Enqueue(string alert)
        {
            recorded.Enqueue("enqueue");
            inner.Enqueue(alert);
        }
    }

    private sealed class FailingDelivery : IOwnerAlertDelivery
    {
        private int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);

        public Task DeliverAsync(string alert, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            return Task.FromException(new InvalidOperationException("delivery unavailable"));
        }
    }
}
