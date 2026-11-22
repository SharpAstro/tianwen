using System.Runtime.Versioning;

namespace TianWen.Shell.Thumbnails
{
    /// <summary>
    /// A read-only, seekable <see cref="Stream"/> over the shell's <c>IStream</c>, so the property handler can
    /// read a header and SEEK past the pixels (the thumbnail path buffers the whole file instead, which a column
    /// read must never do: over a cloud placeholder every byte read is a byte downloaded).
    /// <para>
    /// Only <c>Read</c> and <c>Seek</c> are used, and the file size comes from a <c>Seek</c> to the end rather
    /// than <c>Stat</c>, so no <c>STATSTG</c> is involved. A failed HRESULT is an <see cref="IOException"/>,
    /// which <c>ExplorerFileProperties.Read</c> turns into an empty answer before it can cross the COM boundary.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed unsafe class ComStream : Stream
    {
        private const uint STREAM_SEEK_SET = 0;
        private const uint STREAM_SEEK_END = 2;

        private readonly IStream _stream;
        private long _position;
        private long _length = -1;

        public ComStream(IStream stream) => _stream = stream;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length
        {
            get
            {
                if (_length < 0)
                {
                    ulong end = 0;
                    Check(_stream.Seek(0, STREAM_SEEK_END, &end));
                    _length = (long)end;
                    ulong back = 0;
                    Check(_stream.Seek(_position, STREAM_SEEK_SET, &back));
                }

                return _length;
            }
        }

        public override long Position
        {
            get => _position;
            set
            {
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                ulong now = 0;
                Check(_stream.Seek(value, STREAM_SEEK_SET, &now));
                _position = (long)now;
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => Length + offset,
            };
            Position = target;
            return _position;
        }

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty)
            {
                return 0;
            }

            uint read = 0;
            fixed (byte* p = buffer)
            {
                Check(_stream.Read(p, (uint)buffer.Length, &read));
            }

            _position += read;
            return (int)read;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private static void Check(int hr)
        {
            if (hr < 0)
            {
                throw new IOException("IStream call failed", hr);
            }
        }
    }
}
