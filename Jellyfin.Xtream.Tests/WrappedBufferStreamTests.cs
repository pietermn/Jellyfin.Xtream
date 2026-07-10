using Jellyfin.Xtream.Service;

namespace Jellyfin.Xtream.Tests;

public class WrappedBufferStreamTests
{
    [Fact]
    public async Task ReadAsyncWaitsForAndReturnsWrittenData()
    {
        using WrappedBufferStream buffer = new(1024);
        await using WrappedBufferReadStream reader = new(buffer);
        byte[] destination = new byte[4];

        ValueTask<int> pendingRead = reader.ReadAsync(destination);
        Assert.False(pendingRead.IsCompleted);

        buffer.Write([1, 2, 3, 4]);

        Assert.Equal(4, await pendingRead);
        Assert.Equal([1, 2, 3, 4], destination);
        Assert.Equal(4, reader.TotalBytesRead);
    }

    [Fact]
    public async Task CompleteDrainsBufferedDataThenReturnsEndOfStream()
    {
        using WrappedBufferStream buffer = new(1024);
        buffer.Write([1, 2, 3]);
        buffer.Complete();
        await using WrappedBufferReadStream reader = new(buffer);
        byte[] destination = new byte[8];

        Assert.Equal(3, await reader.ReadAsync(destination));
        Assert.Equal([1, 2, 3], destination[..3]);
        Assert.Equal(0, await reader.ReadAsync(destination));
    }

    [Fact]
    public async Task CompleteUnblocksPendingSynchronousAndAsynchronousReads()
    {
        using WrappedBufferStream buffer = new(1024);
        await using WrappedBufferReadStream synchronousReader = new(buffer);
        await using WrappedBufferReadStream asynchronousReader = new(buffer);
        Task<int> synchronousRead = Task.Run(() => synchronousReader.Read(new byte[1], 0, 1));
        ValueTask<int> asynchronousRead = asynchronousReader.ReadAsync(new byte[1]);

        await Task.Delay(20);
        buffer.Complete();

        Assert.Equal(0, await synchronousRead.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(0, await asynchronousRead.AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task FailureIsRaisedAfterBufferedDataIsDrained()
    {
        using WrappedBufferStream buffer = new(1024);
        buffer.Write([1, 2]);
        buffer.Complete(new IOException("upstream failed"));
        await using WrappedBufferReadStream reader = new(buffer);
        byte[] destination = new byte[2];

        Assert.Equal(2, await reader.ReadAsync(destination));
        IOException exception = await Assert.ThrowsAsync<IOException>(
            async () => await reader.ReadExactlyAsync(destination));
        Assert.Equal("upstream failed", exception.Message);
    }

    [Fact]
    public async Task SourceDisposeUnblocksPendingReadWithObjectDisposedException()
    {
        WrappedBufferStream buffer = new(1024);
        await using WrappedBufferReadStream reader = new(buffer);
        ValueTask<int> pendingRead = reader.ReadAsync(new byte[1]);

        buffer.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await pendingRead.AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task ReaderDisposeUnblocksItsPendingReadWithoutAffectingOtherReaders()
    {
        using WrappedBufferStream buffer = new(1024);
        WrappedBufferReadStream disposedReader = new(buffer);
        await using WrappedBufferReadStream activeReader = new(buffer);
        ValueTask<int> pendingRead = disposedReader.ReadAsync(new byte[1]);

        disposedReader.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await pendingRead.AsTask().WaitAsync(TimeSpan.FromSeconds(1)));

        buffer.Write([42]);
        byte[] destination = new byte[1];
        Assert.Equal(1, await activeReader.ReadAsync(destination));
        Assert.Equal(42, destination[0]);
    }

    [Fact]
    public async Task CanceledReadDoesNotPoisonTheReader()
    {
        using WrappedBufferStream buffer = new(1024);
        await using WrappedBufferReadStream reader = new(buffer);
        using CancellationTokenSource cancellationTokenSource = new();
        byte[] canceledDestination = new byte[1];
        Task<int> pendingRead = reader.ReadAsync(canceledDestination, 0, 1, cancellationTokenSource.Token);

        cancellationTokenSource.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pendingRead);

        buffer.Write([7]);
        byte[] destination = new byte[1];
        Assert.Equal(1, await reader.ReadAsync(destination));
        Assert.Equal(7, destination[0]);
    }

    [Fact]
    public async Task SlowReaderDropsStaleBytesAndRemainsWithinTheRing()
    {
        using WrappedBufferStream buffer = new(16);
        await using WrappedBufferReadStream reader = new(buffer);
        buffer.Write(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
        byte[] destination = new byte[4];

        Assert.Equal(4, await reader.ReadAsync(destination));
        Assert.Equal([28, 29, 30, 31], destination);
        Assert.Equal(4, reader.TotalBytesRead);
        Assert.Equal(16, buffer.BufferSize);
    }
}
