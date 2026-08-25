using MemeApiDotNetWrapper;

namespace BeanBot.Discord.Commands;

internal interface IMemeProvider
{
    Task<Meme?> GetMemeAsync(string subreddit, CancellationToken cancellationToken);
}

internal sealed class MemeProvider : IMemeProvider
{
    private readonly Func<string, Task<Meme?>> _getMeme;
    private readonly TimeSpan _timeout;

    public MemeProvider(ExternalMediaCommandOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var memeMachine = new MemeMachine();
        _getMeme = subreddit => memeMachine.GetMemeAsync(
            string.IsNullOrWhiteSpace(subreddit) ? null : subreddit);
        _timeout = options.MemeApiTimeout;
    }

    internal MemeProvider(Func<string, Task<Meme?>> getMeme, TimeSpan timeout)
    {
        _getMeme = getMeme ?? throw new ArgumentNullException(nameof(getMeme));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        _timeout = timeout;
    }

    public Task<Meme?> GetMemeAsync(string subreddit, CancellationToken cancellationToken)
        => ExternalMediaOperationGuard.RunAsync(
            _ => _getMeme(subreddit ?? string.Empty),
            _timeout,
            cancellationToken,
            "Meme API request timed out.");
}
