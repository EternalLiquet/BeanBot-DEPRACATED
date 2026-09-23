namespace BeanBot.Health;

internal enum ApplicationLifecycleState
{
    Starting,
    Ready,
    Draining
}

internal readonly record struct ApplicationReadinessSnapshot(ApplicationLifecycleState State)
{
    public bool IsReady => State == ApplicationLifecycleState.Ready;

    public string StateName => State switch
    {
        ApplicationLifecycleState.Starting => "starting",
        ApplicationLifecycleState.Ready => "ready",
        ApplicationLifecycleState.Draining => "draining",
        _ => "unknown"
    };

    public string StatusMessage => State switch
    {
        ApplicationLifecycleState.Starting => "BeanBot startup is not complete yet.",
        ApplicationLifecycleState.Ready => "BeanBot startup is complete and normal work is being admitted.",
        ApplicationLifecycleState.Draining => "BeanBot is draining for shutdown.",
        _ => "BeanBot lifecycle state is unknown."
    };
}

internal sealed class ApplicationReadinessState
{
    private int _state = (int)ApplicationLifecycleState.Starting;

    public ApplicationReadinessSnapshot CreateSnapshot()
        => new((ApplicationLifecycleState)Volatile.Read(ref _state));

    public void MarkReady()
    {
        _ = Interlocked.CompareExchange(
            ref _state,
            (int)ApplicationLifecycleState.Ready,
            (int)ApplicationLifecycleState.Starting);
    }

    public void BeginDraining()
        => Interlocked.Exchange(ref _state, (int)ApplicationLifecycleState.Draining);
}
