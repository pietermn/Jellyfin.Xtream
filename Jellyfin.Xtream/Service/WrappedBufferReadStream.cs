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
/// A read-only view over a <see cref="WrappedBufferStream"/>.
/// </summary>
public class WrappedBufferReadStream : Stream
{
    private const int MaxReadPrerollBytes = 2 * 1024 * 1024;

    private readonly WrappedBufferStream.ReaderState _reader;
    private readonly WrappedBufferStream _sourceBuffer;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="WrappedBufferReadStream"/> class.
    /// </summary>
    /// <param name="sourceBuffer">The source buffer to read from.</param>
    public WrappedBufferReadStream(WrappedBufferStream sourceBuffer)
    {
        ArgumentNullException.ThrowIfNull(sourceBuffer);
        _sourceBuffer = sourceBuffer;
        _reader = sourceBuffer.CreateReader(MaxReadPrerollBytes);
    }

    /// <summary>
    /// Gets the virtual position in the source buffer.
    /// </summary>
    public long ReadHead => _sourceBuffer.GetReadHead(_reader);

    /// <summary>
    /// Gets the number of bytes that have been returned to this reader.
    /// </summary>
    public long TotalBytesRead => _sourceBuffer.GetTotalBytesRead(_reader);

    /// <inheritdoc />
    public override long Position
    {
        get => ReadHead % _sourceBuffer.BufferSize;
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override bool CanRead => !_disposed;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        return _sourceBuffer.Read(_reader, buffer, MaxReadPrerollBytes);
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _sourceBuffer.ReadAsync(_reader, buffer, MaxReadPrerollBytes, cancellationToken);
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Flush()
    {
        // This is a read-only stream.
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _sourceBuffer.DisposeReader(_reader);
        }

        base.Dispose(disposing);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
