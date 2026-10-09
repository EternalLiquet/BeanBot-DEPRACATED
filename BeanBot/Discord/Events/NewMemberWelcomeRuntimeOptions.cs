namespace BeanBot.Discord.Events;

internal sealed record NewMemberWelcomeRuntimeOptions(
    int MaximumOutstanding,
    int WorkerCount,
    int MaximumDiscordOperations,
    TimeSpan OperationTimeout,
    TimeSpan ShutdownDrainTimeout,
    TimeSpan ShutdownCancellationGrace)
{
    internal static NewMemberWelcomeRuntimeOptions Default { get; } = new(
        MaximumOutstanding: 64,
        WorkerCount: 2,
        MaximumDiscordOperations: 4,
        OperationTimeout: TimeSpan.FromSeconds(10),
        ShutdownDrainTimeout: TimeSpan.FromSeconds(5),
        ShutdownCancellationGrace: TimeSpan.FromSeconds(1));

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaximumOutstanding, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(WorkerCount, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(WorkerCount, MaximumOutstanding);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaximumDiscordOperations, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(OperationTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ShutdownDrainTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ShutdownCancellationGrace, TimeSpan.Zero);
    }
}
