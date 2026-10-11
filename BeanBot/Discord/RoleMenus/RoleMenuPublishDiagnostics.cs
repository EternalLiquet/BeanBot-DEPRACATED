using System.Diagnostics;
using BeanBot.Logging;
using Microsoft.Extensions.Logging;

namespace BeanBot.Discord.RoleMenus;

internal sealed class RoleMenuPublishDiagnostics
{
    private readonly ILogger _logger;
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly string _attemptId = Guid.NewGuid().ToString("N");
    private string _activePhase = "validation";
    private long _phaseStarted;

    internal RoleMenuPublishDiagnostics(ILogger logger)
    {
        _logger = logger;
        Start("validation");
    }

    internal void Start(string phase)
    {
        _activePhase = phase;
        _phaseStarted = Stopwatch.GetTimestamp();
        Log("started", 0);
    }

    internal void Complete(string outcome = "completed")
        => Log(outcome, Stopwatch.GetElapsedTime(_phaseStarted).TotalMilliseconds);

    internal async Task<T> TrackAsync<T>(string phase, Func<Task<T>> operation)
    {
        Start(phase);
        try
        {
            var result = await operation();
            Complete();
            return result;
        }
        catch (OperationCanceledException)
        {
            Complete("cancelled");
            throw;
        }
        catch (Exception)
        {
            Complete("failed");
            throw;
        }
    }

    internal async Task TrackAsync(string phase, Func<Task> operation)
    {
        await TrackAsync(phase, async () =>
        {
            await operation();
            return true;
        });
    }

    internal void Finish(string outcome)
        => Log(outcome, Stopwatch.GetElapsedTime(_phaseStarted).TotalMilliseconds);

    private void Log(string outcome, double phaseElapsedMs)
    {
        var elapsedMs = Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
        if (outcome is "timeout" or "failed" or "cancelled" or "shutdown_cancelled")
        {
            BeanBotLog.RoleMenuPublishPhaseWarning(
                _logger, _attemptId, _activePhase, outcome, elapsedMs, phaseElapsedMs);
        }
        else
        {
            BeanBotLog.RoleMenuPublishPhase(
                _logger, _attemptId, _activePhase, outcome, elapsedMs, phaseElapsedMs);
        }
    }
}
