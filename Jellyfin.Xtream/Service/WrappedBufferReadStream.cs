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

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Stream which writes to a self-overwriting internal buffer.
/// </summary>
public class WrappedBufferReadStream : Stream
{
    private const int MaxReadPrerollBytes = 2 * 1024 * 1024;

    private readonly WrappedBufferStream _sourceBuffer;

    private readonly long _initialReadHead;

    private long _readHead;

    /// <summary>
    /// Initializes a new instance of the <see cref="WrappedBufferReadStream"/> class.
    /// </summary>
    /// <param name="sourceBuffer">The source buffer to read from.</param>
    public WrappedBufferReadStream(WrappedBufferStream sourceBuffer)
    {
        _sourceBuffer = sourceBuffer;
        _initialReadHead = sourceBuffer.GetRecentReadHead(MaxReadPrerollBytes);
        _readHead = _initialReadHead;
    }

    /// <summary>
    /// Gets the virtual position in the source buffer.
    /// </summary>
    public long ReadHead => _readHead;

    /// <summary>
    /// Gets the number of bytes that have been written to this stream.
    /// </summary>
    public long TotalBytesRead { get => ReadHead - _initialReadHead; }

    /// <inheritdoc />
    public override long Position
    {
        get
        {
            return ReadHead % _sourceBuffer.BufferSize;
        }

        set
        {
        }
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override bool CanSeek => false;

#pragma warning disable CA1065
    /// <inheritdoc />
    public override long Length { get => throw new NotImplementedException(); }
#pragma warning restore CA1065

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        return _sourceBuffer.Read(ref _readHead, buffer, offset, count, MaxReadPrerollBytes);
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        return _sourceBuffer.Read(ref _readHead, buffer, MaxReadPrerollBytes);
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public override void SetLength(long value)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public override void Flush()
    {
        // Do nothing
    }
}
