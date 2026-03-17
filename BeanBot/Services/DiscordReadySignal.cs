namespace BeanBot.Services;

public sealed class DiscordReadySignal
{
    private readonly TaskCompletionSource _readySource = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void MarkReady()
        => _readySource.TrySetResult();

    public Task WaitAsync(CancellationToken cancellationToken)
        => _readySource.Task.WaitAsync(cancellationToken);
}
