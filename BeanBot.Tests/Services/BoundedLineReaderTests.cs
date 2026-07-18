using BeanBot.Services;
using System.Text;
using Xunit;

namespace BeanBot.Tests.Services;

public class BoundedLineReaderTests
{
    [Fact]
    public async Task ReadLineAsync_AcceptsLineExactlyAtLimit()
    {
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("12345\r\n"));
        var reader = new BoundedLineReader(stream);

        var line = await reader.ReadLineAsync(5, CancellationToken.None);

        Assert.Equal("12345", line);
    }

    [Fact]
    public async Task ReadLineAsync_RejectsLineOneCharacterOverLimit()
    {
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("123456\r\n"));
        var reader = new BoundedLineReader(stream);

        await Assert.ThrowsAsync<HttpLineTooLongException>(() =>
            reader.ReadLineAsync(5, CancellationToken.None));
    }

    [Fact]
    public async Task ReadLineAsync_CancelsUnderlyingPartialRead()
    {
        await using var stream = new NeverCompletingStream();
        var reader = new BoundedLineReader(stream);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reader.ReadLineAsync(100, cancellation.Token));

        Assert.True(stream.ObservedCancellation);
    }

    private sealed class NeverCompletingStream : Stream
    {
        public bool ObservedCancellation { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
            catch (OperationCanceledException)
            {
                ObservedCancellation = true;
                throw;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
    }
}
