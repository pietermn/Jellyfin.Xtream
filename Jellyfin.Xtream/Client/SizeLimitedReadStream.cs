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
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Xtream.Client;

/// <summary>
/// Enforces a byte limit while preserving streaming reads from an upstream response.
/// </summary>
internal sealed class SizeLimitedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _maximumBytes;
    private readonly CancellationToken _readCancellationToken;
    private long _bytesRead;

    /// <summary>
    /// Initializes a new instance of the <see cref="SizeLimitedReadStream"/> class.
    /// </summary>
    /// <param name="inner">The response stream.</param>
    /// <param name="maximumBytes">The maximum number of bytes that may be read.</param>
    /// <param name="readCancellationToken">Cancels synchronous parser reads backed by asynchronous network reads.</param>
    public SizeLimitedReadStream(
        Stream inner,
        long maximumBytes,
        CancellationToken readCancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        _inner = inner;
        _maximumBytes = maximumBytes;
        _readCancellationToken = readCancellationToken;
    }

    /// <inheritdoc />
    public override bool CanRead => _inner.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = _inner.ReadAsync(
                buffer.AsMemory(offset, GetReadCount(count)),
                _readCancellationToken)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        RecordRead(read);
        return read;
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        int readCount = GetReadCount(buffer.Length);
        if (readCount == 0)
        {
            return 0;
        }

        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(readCount);
        try
        {
            int read = _inner.ReadAsync(
                    rentedBuffer.AsMemory(0, readCount),
                    _readCancellationToken)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            rentedBuffer.AsSpan(0, read).CopyTo(buffer);
            RecordRead(read);
            return read;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBuffer);
        }
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource? linkedTokenSource = CreateLinkedTokenSource(cancellationToken);
        CancellationToken effectiveToken = linkedTokenSource?.Token
            ?? (_readCancellationToken.CanBeCanceled ? _readCancellationToken : cancellationToken);
        int read = await _inner.ReadAsync(
            buffer[..GetReadCount(buffer.Length)],
            effectiveToken).ConfigureAwait(false);
        RecordRead(read);
        return read;
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        return ReadArrayAsync(buffer, offset, count, cancellationToken);
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private int GetReadCount(int requestedCount)
    {
        long remaining = _maximumBytes - _bytesRead;
        return (int)Math.Min(requestedCount, Math.Max(0, remaining) + 1);
    }

    private void RecordRead(int read)
    {
        _bytesRead += read;
        if (_bytesRead > _maximumBytes)
        {
            throw new InvalidDataException("The response exceeded the configured size limit.");
        }
    }

    private async Task<int> ReadArrayAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource? linkedTokenSource = CreateLinkedTokenSource(cancellationToken);
        CancellationToken effectiveToken = linkedTokenSource?.Token
            ?? (_readCancellationToken.CanBeCanceled ? _readCancellationToken : cancellationToken);
        int read = await _inner.ReadAsync(
            buffer.AsMemory(offset, GetReadCount(count)),
            effectiveToken).ConfigureAwait(false);
        RecordRead(read);
        return read;
    }

    private CancellationTokenSource? CreateLinkedTokenSource(CancellationToken cancellationToken)
    {
        if (!_readCancellationToken.CanBeCanceled
            || !cancellationToken.CanBeCanceled
            || cancellationToken == _readCancellationToken)
        {
            return null;
        }

        return CancellationTokenSource.CreateLinkedTokenSource(_readCancellationToken, cancellationToken);
    }
}
