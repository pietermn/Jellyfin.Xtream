using Jellyfin.Xtream.Client;

namespace Jellyfin.Xtream.Tests;

public sealed class SizeLimitedReadStreamTests
{
    [Fact]
    public void ExactLimitCanBeReadToEnd()
    {
        using SizeLimitedReadStream stream = new(new MemoryStream([1, 2, 3]), 3);
        byte[] buffer = new byte[4];

        Assert.Equal(3, stream.Read(buffer, 0, buffer.Length));
        Assert.Equal(0, stream.Read(buffer, 0, buffer.Length));
    }

    [Fact]
    public void ChunkedResponseOverLimitIsRejected()
    {
        using SizeLimitedReadStream stream = new(new MemoryStream([1, 2, 3, 4]), 3);

        Assert.Throws<InvalidDataException>(() => stream.CopyTo(Stream.Null));
    }

    [Fact]
    public async Task AsyncResponseOverLimitIsRejected()
    {
        await using SizeLimitedReadStream stream = new(new MemoryStream([1, 2, 3, 4]), 3);
        byte[] buffer = new byte[4];

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await stream.ReadExactlyAsync(buffer, CancellationToken.None));
    }

    [Fact]
    public async Task SynchronousParserReadUsesCancelableAsyncUpstreamRead()
    {
        AsyncOnlyBlockingStream upstream = new();
        using CancellationTokenSource cancellationTokenSource = new();
        using SizeLimitedReadStream stream = new(upstream, 1024, cancellationTokenSource.Token);
        byte[] buffer = new byte[16];

        Task<int> read = Task.Run(() => stream.Read(buffer, 0, buffer.Length));
        await upstream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(0, upstream.SynchronousReadCalls);
    }

    private sealed class AsyncOnlyBlockingStream : Stream
    {
        private int _synchronousReadCalls;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int SynchronousReadCalls => _synchronousReadCalls;

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Interlocked.Increment(ref _synchronousReadCalls);
            throw new InvalidOperationException("Synchronous upstream reads are not cancellation-safe.");
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
