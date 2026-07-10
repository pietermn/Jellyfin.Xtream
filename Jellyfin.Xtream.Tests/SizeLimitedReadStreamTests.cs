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
}
