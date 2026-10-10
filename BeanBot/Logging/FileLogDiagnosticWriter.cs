namespace BeanBot.Logging;

internal sealed class FileLogDiagnosticWriter
{
    private readonly object _sync = new();
    private readonly Action<string> _writeDiagnostic;
    private long _pendingDropped;
    private long _latestDroppedTotal;
    private string? _pendingShutdownMessage;
    private bool _running;

    internal static FileLogDiagnosticWriter ConsoleError { get; } = new(
        static message => Console.Error.WriteLine(message));

    internal FileLogDiagnosticWriter(Action<string> writeDiagnostic)
    {
        _writeDiagnostic = writeDiagnostic ?? throw new ArgumentNullException(nameof(writeDiagnostic));
    }

    internal void ReportDrops(long newlyDropped, long totalDropped)
    {
        lock (_sync)
        {
            _pendingDropped += newlyDropped;
            _latestDroppedTotal = totalDropped;
            StartWriterUnsafe();
        }
    }

    internal void ReportShutdown(string message)
    {
        lock (_sync)
        {
            _pendingShutdownMessage = message;
            StartWriterUnsafe();
        }
    }

    private void StartWriterUnsafe()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _ = Task.Run(WritePending);
    }

    private void WritePending()
    {
        while (true)
        {
            string message;
            lock (_sync)
            {
                if (_pendingDropped > 0)
                {
                    message = FormattableString.Invariant(
                        $"BeanBot persistent file logging dropped {_pendingDropped} log event(s) because the bounded async buffer was full ({_latestDroppedTotal} total dropped since startup).");
                    _pendingDropped = 0;
                }
                else if (_pendingShutdownMessage is not null)
                {
                    message = _pendingShutdownMessage;
                    _pendingShutdownMessage = null;
                }
                else
                {
                    _running = false;
                    return;
                }
            }

            try
            {
                _writeDiagnostic(message);
            }
            catch (Exception)
            {
                // Diagnostics are best effort; never re-enter Serilog or fault the single writer.
            }
        }
    }
}
