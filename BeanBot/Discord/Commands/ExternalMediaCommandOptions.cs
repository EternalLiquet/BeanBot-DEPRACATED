namespace BeanBot.Discord.Commands;

public sealed class ExternalMediaCommandOptions
{
    internal static ExternalMediaCommandOptions Default { get; } = new(
        imageDownloadTimeout: TimeSpan.FromSeconds(10),
        discordUploadTimeout: TimeSpan.FromSeconds(10),
        memeApiTimeout: TimeSpan.FromSeconds(10),
        maxImageBytes: 8 * 1024 * 1024);

    internal ExternalMediaCommandOptions(
        TimeSpan imageDownloadTimeout,
        TimeSpan discordUploadTimeout,
        TimeSpan memeApiTimeout,
        int maxImageBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(imageDownloadTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(discordUploadTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(memeApiTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxImageBytes, 0);

        ImageDownloadTimeout = imageDownloadTimeout;
        DiscordUploadTimeout = discordUploadTimeout;
        MemeApiTimeout = memeApiTimeout;
        MaxImageBytes = maxImageBytes;
    }

    internal TimeSpan ImageDownloadTimeout { get; }

    internal TimeSpan DiscordUploadTimeout { get; }

    internal TimeSpan MemeApiTimeout { get; }

    internal int MaxImageBytes { get; }
}
