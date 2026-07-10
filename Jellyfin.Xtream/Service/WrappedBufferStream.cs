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
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Stream which writes to a self-overwriting internal buffer.
/// </summary>
public class WrappedBufferStream : Stream
{
    private readonly object _syncRoot = new();
    private bool _completed;
    private bool _disposed;
    private ExceptionDispatchInfo? _failure;
    private TaskCompletionSource? _stateChanged;
    private long _totalBytesWritten;

    /// <summary>
    /// Initializes a new instance of the <see cref="WrappedBufferStream"/> class.
    /// </summary>
    /// <param name="bufferSize">Size in bytes of the internal buffer.</param>
    public WrappedBufferStream(int bufferSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);
        Buffer = new byte[bufferSize];
    }

    /// <summary>
    /// Gets the maximal size in bytes of read/write chunks.
    /// </summary>
    public int BufferSize => Buffer.Length;

#pragma warning disable CA1819
    /// <summary>
    /// Gets the internal buffer.
    /// </summary>
    public byte[] Buffer { get; }
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
                ThrowIfDisposedLocked();
                return _totalBytesWritten % BufferSize;
            }
        }

        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override bool CanRead => false;

    /// <inheritdoc />
    public override bool CanWrite
    {
        get
        {
            lock (_syncRoot)
            {
                return !_completed && !_disposed;
            }
        }
    }

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (buffer.Length - offset < count)
        {
            throw new ArgumentException("Offset and count exceed the buffer length.", nameof(count));
        }

        Write(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        lock (_syncRoot)
        {
            ThrowIfNotWritableLocked();
            WriteLocked(buffer);
            SignalStateChangedLocked();
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
        // Nothing is buffered outside the ring.
    }

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    internal ReaderState CreateReader(int maxPrerollBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxPrerollBytes);
        lock (_syncRoot)
        {
            ThrowIfDisposedLocked();
            return new ReaderState(GetRecentReadHeadLocked(maxPrerollBytes));
        }
    }

    internal long GetReadHead(ReaderState reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        lock (_syncRoot)
        {
            return reader.ReadHead;
        }
    }

    internal long GetTotalBytesRead(ReaderState reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        lock (_syncRoot)
        {
            return reader.TotalBytesRead;
        }
    }

    internal int Read(ReaderState reader, Span<byte> buffer, int maxPrerollBytes)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfNegative(maxPrerollBytes);
        if (buffer.Length == 0)
        {
            return 0;
        }

        lock (_syncRoot)
        {
            while (true)
            {
                int bytesRead = TryReadLocked(reader, buffer, maxPrerollBytes);
                if (bytesRead >= 0)
                {
                    return bytesRead;
                }

                Monitor.Wait(_syncRoot);
            }
        }
    }

    internal async ValueTask<int> ReadAsync(
        ReaderState reader,
        Memory<byte> buffer,
        int maxPrerollBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfNegative(maxPrerollBytes);
        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.Length == 0)
        {
            return 0;
        }

        while (true)
        {
            Task stateChanged;
            lock (_syncRoot)
            {
                int bytesRead = TryReadLocked(reader, buffer.Span, maxPrerollBytes);
                if (bytesRead >= 0)
                {
                    return bytesRead;
                }

                _stateChanged ??= CreateStateChangedSource();
                stateChanged = _stateChanged.Task;
            }

            await stateChanged.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal void DisposeReader(ReaderState reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        lock (_syncRoot)
        {
            if (reader.IsDisposed)
            {
                return;
            }

            reader.IsDisposed = true;
            SignalStateChangedLocked();
        }
    }

    internal void Complete(Exception? failure = null)
    {
        lock (_syncRoot)
        {
            if (_completed || _disposed)
            {
                return;
            }

            _failure = failure is null ? null : ExceptionDispatchInfo.Capture(failure);
            _completed = true;
            SignalStateChangedLocked();
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_syncRoot)
            {
                if (!_disposed)
                {
                    _disposed = true;
                    _completed = true;
                    SignalStateChangedLocked();
                }
            }
        }

        base.Dispose(disposing);
    }

    private static TaskCompletionSource CreateStateChangedSource()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int TryReadLocked(ReaderState reader, Span<byte> buffer, int maxPrerollBytes)
    {
        ObjectDisposedException.ThrowIf(reader.IsDisposed, reader);

        ThrowIfDisposedLocked();

        long gap = _totalBytesWritten - reader.ReadHead;
        if (gap > BufferSize)
        {
            // Live playback is better served by dropping stale bytes than by failing the stream.
            reader.ReadHead = GetRecentReadHeadLocked(maxPrerollBytes);
            gap = _totalBytesWritten - reader.ReadHead;
        }

        if (gap > 0)
        {
            int bytesRead = CopyToReaderLocked(reader, buffer, gap);
            reader.TotalBytesRead += bytesRead;
            return bytesRead;
        }

        if (_failure is not null)
        {
            _failure.Throw();
        }

        return _completed ? 0 : -1;
    }

    private int CopyToReaderLocked(ReaderState reader, Span<byte> destination, long gap)
    {
        int canCopy = (int)Math.Min(destination.Length, gap);
        int bytesRead = 0;
        while (bytesRead < canCopy)
        {
            int position = (int)(reader.ReadHead % BufferSize);
            int readable = Math.Min(canCopy - bytesRead, BufferSize - position);
            Buffer.AsSpan(position, readable).CopyTo(destination.Slice(bytesRead, readable));
            bytesRead += readable;
            reader.ReadHead += readable;
        }

        return bytesRead;
    }

    private void WriteLocked(ReadOnlySpan<byte> source)
    {
        int bytesWritten = 0;
        while (bytesWritten < source.Length)
        {
            int position = (int)(_totalBytesWritten % BufferSize);
            int writable = Math.Min(source.Length - bytesWritten, BufferSize - position);
            source.Slice(bytesWritten, writable).CopyTo(Buffer.AsSpan(position, writable));
            bytesWritten += writable;
            _totalBytesWritten += writable;
        }
    }

    private long GetRecentReadHeadLocked(int maxPrerollBytes)
    {
        int prerollBytes = Math.Min(maxPrerollBytes, BufferSize / 4);
        return Math.Max(0, _totalBytesWritten - prerollBytes);
    }

    private void SignalStateChangedLocked()
    {
        TaskCompletionSource? previous = _stateChanged;
        _stateChanged = null;
        Monitor.PulseAll(_syncRoot);
        previous?.TrySetResult();
    }

    private void ThrowIfDisposedLocked()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void ThrowIfNotWritableLocked()
    {
        ThrowIfDisposedLocked();
        if (_completed)
        {
            throw new InvalidOperationException("The buffer has already completed.");
        }
    }

    internal sealed class ReaderState(long readHead)
    {
        public bool IsDisposed { get; set; }

        public long ReadHead { get; set; } = readHead;

        public long TotalBytesRead { get; set; }
    }
}
