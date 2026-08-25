using System.Buffers;
using System.Net.Http.Headers;

namespace BeanBot.Discord.Commands;

public interface IExternalImageClient
{
    Task<byte[]> DownloadImageAsync(Uri imageUrl, CancellationToken cancellationToken);
}

internal sealed class ExternalImageClient : IExternalImageClient, IDisposable
{
    private const int ReadBufferSize = 64 * 1024;

    private readonly HttpClient _httpClient;
    private readonly ExternalMediaCommandOptions _options;
    private readonly bool _ownsHttpClient;

    public ExternalImageClient(ExternalMediaCommandOptions options)
        : this(new HttpClient(), options, ownsHttpClient: true)
    {
    }

    internal ExternalImageClient(
        HttpClient httpClient,
        ExternalMediaCommandOptions options,
        bool ownsHttpClient = false)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _ownsHttpClient = ownsHttpClient;
    }

    public Task<byte[]> DownloadImageAsync(Uri imageUrl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(imageUrl);
        if (!imageUrl.IsAbsoluteUri)
        {
            throw new ArgumentException("External image URL must be absolute.", nameof(imageUrl));
        }

        return ExternalMediaOperationGuard.RunAsync(
            token => DownloadImageCoreAsync(imageUrl, token),
            _options.ImageDownloadTimeout,
            cancellationToken,
            "External image download timed out.");
    }

    private async Task<byte[]> DownloadImageCoreAsync(Uri imageUrl, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, imageUrl);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();
        EnsureImageContentType(response.Content.Headers.ContentType);

        var declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength > _options.MaxImageBytes)
        {
            throw new ExternalMediaPayloadTooLargeException(_options.MaxImageBytes);
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream(GetInitialCapacity(declaredLength));
        var bufferSize = (int)Math.Min(ReadBufferSize, (long)_options.MaxImageBytes + 1);
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            var totalBytes = 0;
            while (true)
            {
                var remainingWithSentinel = (long)_options.MaxImageBytes - totalBytes + 1;
                var bytesToRead = (int)Math.Min(buffer.Length, remainingWithSentinel);
                var bytesRead = await source.ReadAsync(
                    buffer.AsMemory(0, bytesToRead),
                    cancellationToken);

                if (bytesRead == 0)
                {
                    break;
                }

                if (bytesRead > _options.MaxImageBytes - totalBytes)
                {
                    throw new ExternalMediaPayloadTooLargeException(_options.MaxImageBytes);
                }

                await destination.WriteAsync(
                    buffer.AsMemory(0, bytesRead),
                    cancellationToken);
                totalBytes += bytesRead;
            }

            return destination.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private int GetInitialCapacity(long? declaredLength)
    {
        if (declaredLength is > 0 and <= int.MaxValue)
        {
            return (int)declaredLength.Value;
        }

        return Math.Min(64 * 1024, _options.MaxImageBytes);
    }

    private static void EnsureImageContentType(MediaTypeHeaderValue? contentType)
    {
        var mediaType = contentType?.MediaType;
        if (mediaType is null || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("External media response was not an image.");
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}

internal sealed class ExternalMediaPayloadTooLargeException : IOException
{
    internal ExternalMediaPayloadTooLargeException(int maximumBytes)
        : base($"External image exceeded the {maximumBytes}-byte download limit.")
    {
    }
}
