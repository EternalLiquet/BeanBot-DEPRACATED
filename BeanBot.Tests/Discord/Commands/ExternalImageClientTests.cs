using System.Net;
using System.Net.Http.Headers;
using BeanBot.Discord.Commands;
using Xunit;

namespace BeanBot.Tests.Discord.Commands;

public class ExternalImageClientTests
{
    [Fact]
    public async Task DownloadImageAsync_NormalImage_ReturnsContent()
    {
        byte[] expected = [1, 2, 3, 4];
        using var httpClient = CreateHttpClient(_ => CreateResponse(expected, "image/png"));
        using var client = new ExternalImageClient(httpClient, CreateOptions(maxImageBytes: 16));

        var result = await client.DownloadImageAsync(
            new Uri("https://example.test/image.png"),
            CancellationToken.None);

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task DownloadImageAsync_DeclaredLengthExceedsLimit_RejectsPayload()
    {
        using var httpClient = CreateHttpClient(_ => CreateResponse([0, 0, 0, 0, 0], "image/png"));
        using var client = new ExternalImageClient(httpClient, CreateOptions(maxImageBytes: 4));

        await Assert.ThrowsAsync<ExternalMediaPayloadTooLargeException>(() =>
            client.DownloadImageAsync(
                new Uri("https://example.test/image.png"),
                CancellationToken.None));
    }

    [Fact]
    public async Task DownloadImageAsync_UnknownLengthExceedsLimit_RejectsPayload()
    {
        var stream = new NonSeekableReadStream([1, 2, 3, 4, 5]);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");

        using var httpClient = CreateHttpClient(_ => response);
        using var client = new ExternalImageClient(httpClient, CreateOptions(maxImageBytes: 4));

        await Assert.ThrowsAsync<ExternalMediaPayloadTooLargeException>(() =>
            client.DownloadImageAsync(
                new Uri("https://example.test/chunked-image"),
                CancellationToken.None));
    }

    [Fact]
    public async Task DownloadImageAsync_NonImageResponse_RejectsPayload()
    {
        using var httpClient = CreateHttpClient(_ => CreateResponse([1], "text/plain"));
        using var client = new ExternalImageClient(httpClient, CreateOptions());

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.DownloadImageAsync(
                new Uri("https://example.test/not-image"),
                CancellationToken.None));
    }

    [Fact]
    public async Task DownloadImageAsync_StalledBody_TimesOut()
    {
        var stalledStream = new StalledReadStream();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stalledStream)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");

        using var httpClient = CreateHttpClient(_ => response);
        using var client = new ExternalImageClient(
            httpClient,
            CreateOptions(imageDownloadTimeout: TimeSpan.FromMilliseconds(50)));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            client.DownloadImageAsync(
                new Uri("https://example.test/stalled-image"),
                CancellationToken.None));

        stalledStream.Fail(new InvalidOperationException("late read failure"));
        await Task.Yield();
    }

    [Fact]
    public async Task DownloadImageAsync_CallerCancellation_IsNotReportedAsTimeout()
    {
        var stalledStream = new StalledReadStream();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stalledStream)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");

        using var httpClient = CreateHttpClient(_ => response);
        using var client = new ExternalImageClient(
            httpClient,
            CreateOptions(imageDownloadTimeout: TimeSpan.FromSeconds(5)));
        using var cancellation = new CancellationTokenSource();

        var download = client.DownloadImageAsync(
            new Uri("https://example.test/stalled-image"),
            cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        stalledStream.Fail(new InvalidOperationException("late read failure"));
        await Task.Yield();
    }

    private static ExternalMediaCommandOptions CreateOptions(
        TimeSpan? imageDownloadTimeout = null,
        int maxImageBytes = 1024)
        => new(
            imageDownloadTimeout ?? TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            maxImageBytes);

    private static HttpClient CreateHttpClient(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        => new(new StubHttpMessageHandler((request, _) => Task.FromResult(responseFactory(request))));

    private static HttpResponseMessage CreateResponse(byte[] content, string mediaType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return response;
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => sendAsync(request, cancellationToken);
    }

    private sealed class NonSeekableReadStream(byte[] content) : Stream
    {
        private readonly MemoryStream _inner = new(content, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class StalledReadStream : Stream
    {
        private readonly TaskCompletionSource<int> _readCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        internal void Fail(Exception exception) => _readCompletion.TrySetException(exception);

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => new(_readCompletion.Task);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
