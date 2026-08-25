using Discord;

namespace BeanBot.Discord.Commands;

internal static class BoundedDiscordFileSender
{
    internal static async Task SendAsync(
        Func<Stream, RequestOptions, Task> sendFile,
        byte[] content,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sendFile);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        using var contentStream = new MemoryStream(content, writable: false);
        await ExternalMediaOperationGuard.RunAsync(
            token => sendFile(
                contentStream,
                new RequestOptions
                {
                    CancelToken = token
                }),
            timeout,
            cancellationToken,
            "Discord media upload timed out.");
    }
}
