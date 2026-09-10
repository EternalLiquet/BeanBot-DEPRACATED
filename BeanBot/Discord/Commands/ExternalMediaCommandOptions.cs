namespace BeanBot.Discord.Commands;

public sealed class ExternalMediaCommandOptions
{
    private static readonly TimeSpan DefaultAdmissionCooldown = TimeSpan.FromSeconds(5);
    private const int DefaultAdmissionCapacity = 256;

    internal static ExternalMediaCommandOptions Default { get; } = new(
        imageDownloadTimeout: TimeSpan.FromSeconds(10),
        discordUploadTimeout: TimeSpan.FromSeconds(10),
        memeApiTimeout: TimeSpan.FromSeconds(10),
        maxImageBytes: 8 * 1024 * 1024,
        admissionCooldown: DefaultAdmissionCooldown,
        admissionCapacity: DefaultAdmissionCapacity);

    internal ExternalMediaCommandOptions(
        TimeSpan imageDownloadTimeout,
        TimeSpan discordUploadTimeout,
        TimeSpan memeApiTimeout,
        int maxImageBytes)
        : this(
            imageDownloadTimeout,
            discordUploadTimeout,
            memeApiTimeout,
            maxImageBytes,
            DefaultAdmissionCooldown,
            DefaultAdmissionCapacity)
    {
    }

    internal ExternalMediaCommandOptions(
        TimeSpan imageDownloadTimeout,
        TimeSpan discordUploadTimeout,
        TimeSpan memeApiTimeout,
        int maxImageBytes,
        TimeSpan admissionCooldown,
        int admissionCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(imageDownloadTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(discordUploadTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(memeApiTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxImageBytes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(admissionCooldown, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(admissionCapacity, 0);

        ImageDownloadTimeout = imageDownloadTimeout;
        DiscordUploadTimeout = discordUploadTimeout;
        MemeApiTimeout = memeApiTimeout;
        MaxImageBytes = maxImageBytes;
        AdmissionCooldown = admissionCooldown;
        AdmissionCapacity = admissionCapacity;
    }

    internal TimeSpan ImageDownloadTimeout { get; }

    internal TimeSpan DiscordUploadTimeout { get; }

    internal TimeSpan MemeApiTimeout { get; }

    internal int MaxImageBytes { get; }

    internal TimeSpan AdmissionCooldown { get; }

    internal int AdmissionCapacity { get; }
}
