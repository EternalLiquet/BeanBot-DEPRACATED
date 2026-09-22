using Serilog.Sinks.Async;

namespace BeanBot.Logging;

internal sealed class FileLogDropMonitor : IAsyncLogEventSinkMonitor, IDisposable
{
    private readonly object _sync = new();
    private readonly TimeSpan _reportInterval;
    private readonly Action<string> _writeDiagnostic;
    private IAsyncLogEventSinkInspector? _inspector;
    private Timer? _timer;
    private long _lastReportedDroppedMessagesCount;
    private bool _disposed;

    internal FileLogDropMonitor()
        : this(
            FileLogPolicy.DropReportInterval,
            static message => Console.Error.WriteLine(message))
    {
    }

    internal FileLogDropMonitor(TimeSpan reportInterval, Action<string> writeDiagnostic)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(reportInterval, TimeSpan.Zero);
        _reportInterval = reportInterval;
        _writeDiagnostic = writeDiagnostic ?? throw new ArgumentNullException(nameof(writeDiagnostic));
    }

    public void StartMonitoring(IAsyncLogEventSinkInspector inspector)
    {
        ArgumentNullException.ThrowIfNull(inspector);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_inspector is not null)
            {
                throw new InvalidOperationException("The file-log async sink is already being monitored.");
            }

            _inspector = inspector;
            Interlocked.Exchange(
                ref _lastReportedDroppedMessagesCount,
                inspector.DroppedMessagesCount);
            _timer = new Timer(
                static state => ((FileLogDropMonitor)state!).CheckNow(),
                this,
                _reportInterval,
                _reportInterval);
        }
    }

    public void StopMonitoring(IAsyncLogEventSinkInspector inspector)
    {
        ArgumentNullException.ThrowIfNull(inspector);

        Timer? timer;
        lock (_sync)
        {
            if (!ReferenceEquals(_inspector, inspector))
            {
                return;
            }

            timer = _timer;
            _timer = null;
            _inspector = null;
            _disposed = true;
        }

        timer?.Dispose();
        ReportNewDrops(inspector);
    }

    public void Dispose()
    {
        Timer? timer;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            timer = _timer;
            _timer = null;
            _inspector = null;
        }

        timer?.Dispose();
    }

    internal long CheckNow()
    {
        IAsyncLogEventSinkInspector? inspector;
        lock (_sync)
        {
            inspector = _inspector;
        }

        if (inspector is null)
        {
            return Interlocked.Read(ref _lastReportedDroppedMessagesCount);
        }

        ReportNewDrops(inspector);
        return inspector.DroppedMessagesCount;
    }

    private void ReportNewDrops(IAsyncLogEventSinkInspector inspector)
    {
        var droppedMessagesCount = inspector.DroppedMessagesCount;
        long previouslyReported;

        while (true)
        {
            previouslyReported = Interlocked.Read(ref _lastReportedDroppedMessagesCount);
            if (droppedMessagesCount <= previouslyReported)
            {
                return;
            }

            if (Interlocked.CompareExchange(
                    ref _lastReportedDroppedMessagesCount,
                    droppedMessagesCount,
                    previouslyReported) == previouslyReported)
            {
                break;
            }
        }

        var newlyDropped = droppedMessagesCount - previouslyReported;
        var message = FormattableString.Invariant(
            $"BeanBot persistent file logging dropped {newlyDropped} log event(s) because the bounded async buffer was full ({droppedMessagesCount} total dropped since startup).");

        try
        {
            _writeDiagnostic(message);
        }
        catch (IOException)
        {
            // File-log loss diagnostics must never recurse back into Serilog or disrupt application work.
        }
        catch (ObjectDisposedException)
        {
            // Console.Error can be unavailable during process teardown; there is no safer fallback here.
        }
    }
}
