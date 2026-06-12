// Copyright (C) 2022  Kevin Jilissen

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Stream which writes to a self-overwriting internal buffer.
/// </summary>
/// <param name="bufferSize">Size in bytes of the internal buffer.</param>
public class WrappedBufferStream(int bufferSize) : Stream
{
    private readonly object _syncRoot = new();

    private long _totalBytesWritten;

    /// <summary>
    /// Gets the maximal size in bytes of read/write chunks.
    /// </summary>
    public int BufferSize { get => Buffer.Length; }

#pragma warning disable CA1819
    /// <summary>
    /// Gets the internal buffer.
    /// </summary>
    public byte[] Buffer { get; } = new byte[bufferSize];
#pragma warning restore CA1819

    /// <summary>
    /// Gets the number of bytes that have been written to this stream.
    /// </summary>
    public long TotalBytesWritten
    {
        get
        {
            lock (_syncRoot)
            {
                return _totalBytesWritten;
            }
        }
    }

    /// <inheritdoc />
    public override long Position
    {
        get
        {
            lock (_syncRoot)
            {
                return _totalBytesWritten % BufferSize;
            }
        }

        set
        {
        }
    }

    /// <inheritdoc />
    public override bool CanRead => false;

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        lock (_syncRoot)
        {
            WriteLocked(buffer, offset, count);
            Monitor.PulseAll(_syncRoot);
        }
    }

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        lock (_syncRoot)
        {
            WriteLocked(buffer);
            Monitor.PulseAll(_syncRoot);
        }
    }

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Flush()
    {
        // Do nothing
    }

    internal long GetRecentReadHead(int maxPrerollBytes)
    {
        lock (_syncRoot)
        {
            return GetRecentReadHeadLocked(maxPrerollBytes);
        }
    }

    internal int Read(ref long readHead, byte[] buffer, int offset, int count, int maxPrerollBytes)
    {
        lock (_syncRoot)
        {
            long gap = WaitForReadableDataLocked(ref readHead, maxPrerollBytes);
            long canCopy = Math.Min(count, gap);
            long read = 0;

            // Copy inside a loop to simplify wrapping logic.
            while (read < canCopy)
            {
                long position = readHead % BufferSize;
                long readable = Math.Min(canCopy - read, BufferSize - position);

                Array.Copy(Buffer, position, buffer, offset + read, readable);
                read += readable;
                readHead += readable;
            }

            return (int)read;
        }
    }

    internal int Read(ref long readHead, Span<byte> buffer, int maxPrerollBytes)
    {
        lock (_syncRoot)
        {
            long gap = WaitForReadableDataLocked(ref readHead, maxPrerollBytes);
            int canCopy = (int)Math.Min(buffer.Length, gap);
            int read = 0;

            // Copy inside a loop to simplify wrapping logic.
            while (read < canCopy)
            {
                int position = (int)(readHead % BufferSize);
                int readable = Math.Min(canCopy - read, BufferSize - position);

                Buffer.AsSpan(position, readable).CopyTo(buffer.Slice(read, readable));
                read += readable;
                readHead += readable;
            }

            return read;
        }
    }

    private void WriteLocked(byte[] buffer, int offset, int count)
    {
        long written = 0;

        // Copy inside a loop to simplify wrapping logic.
        while (written < count)
        {
            long position = _totalBytesWritten % BufferSize;
            long writable = Math.Min(count - written, BufferSize - position);

            Array.Copy(buffer, offset + written, Buffer, position, writable);
            written += writable;
            _totalBytesWritten += writable;
        }
    }

    private void WriteLocked(ReadOnlySpan<byte> buffer)
    {
        int written = 0;

        // Copy inside a loop to simplify wrapping logic.
        while (written < buffer.Length)
        {
            long position = _totalBytesWritten % BufferSize;
            int writable = Math.Min(buffer.Length - written, BufferSize - (int)position);

            buffer.Slice(written, writable).CopyTo(Buffer.AsSpan((int)position, writable));
            written += writable;
            _totalBytesWritten += writable;
        }
    }

    private long GetRecentReadHeadLocked(int maxPrerollBytes)
    {
        int prerollBytes = Math.Min(maxPrerollBytes, BufferSize / 8);
        return Math.Max(0, _totalBytesWritten - prerollBytes);
    }

    private long WaitForReadableDataLocked(ref long readHead, int maxPrerollBytes)
    {
        long gap = _totalBytesWritten - readHead;

        // We cannot return with 0 bytes read, as that indicates the end of the stream has been reached.
        while (gap == 0)
        {
            Monitor.Wait(_syncRoot);
            gap = _totalBytesWritten - readHead;
        }

        if (gap > BufferSize)
        {
            // Live playback is better served by dropping stale bytes than by failing the stream.
            readHead = GetRecentReadHeadLocked(maxPrerollBytes);
            gap = _totalBytesWritten - readHead;
        }

        return gap;
    }
}
