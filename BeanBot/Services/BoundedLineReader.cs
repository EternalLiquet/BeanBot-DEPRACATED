using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BeanBot.Services
{
    internal sealed class BoundedLineReader
    {
        private const int ReadBufferSize = 1024;
        private readonly Stream _stream;
        private readonly byte[] _readBuffer = new byte[ReadBufferSize];
        private int _bufferOffset;
        private int _bufferLength;

        public BoundedLineReader(Stream stream)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        }

        public async Task<string> ReadLineAsync(int maximumLength, CancellationToken cancellationToken)
        {
            if (maximumLength < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumLength));
            }

            var lineBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, maximumLength + 1));
            var lineLength = 0;
            try
            {
                while (true)
                {
                    var nextByte = await ReadByteAsync(cancellationToken);
                    if (nextByte < 0)
                    {
                        return lineLength == 0
                            ? null
                            : Encoding.ASCII.GetString(lineBuffer, 0, lineLength);
                    }

                    if (nextByte == '\n')
                    {
                        if (lineLength > 0 && lineBuffer[lineLength - 1] == '\r')
                        {
                            lineLength--;
                        }

                        return Encoding.ASCII.GetString(lineBuffer, 0, lineLength);
                    }

                    if (lineLength < maximumLength)
                    {
                        lineBuffer[lineLength++] = (byte)nextByte;
                        continue;
                    }

                    // Permit one trailing CR beyond the content limit so a CRLF-terminated
                    // line whose content is exactly at the limit remains valid.
                    if (lineLength == maximumLength && nextByte == '\r')
                    {
                        lineBuffer[lineLength++] = (byte)nextByte;
                        continue;
                    }

                    throw new HttpLineTooLongException(maximumLength);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(lineBuffer);
            }
        }

        private async ValueTask<int> ReadByteAsync(CancellationToken cancellationToken)
        {
            if (_bufferOffset >= _bufferLength)
            {
                _bufferLength = await _stream.ReadAsync(_readBuffer.AsMemory(), cancellationToken);
                _bufferOffset = 0;
                if (_bufferLength == 0)
                {
                    return -1;
                }
            }

            return _readBuffer[_bufferOffset++];
        }
    }

    internal sealed class HttpLineTooLongException : Exception
    {
        public HttpLineTooLongException(int maximumLength)
            : base($"HTTP line exceeded the {maximumLength}-character limit.")
        {
        }
    }

    internal sealed class HttpHeadersTooLargeException : Exception
    {
        public HttpHeadersTooLargeException(string message)
            : base(message)
        {
        }

        public HttpHeadersTooLargeException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    internal sealed class MalformedHttpHeaderException : Exception
    {
        public MalformedHttpHeaderException(string message)
            : base(message)
        {
        }
    }
}
