using System.Net;
using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Service;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Xtream.Tests;

public class RestreamTests
{
    [Fact]
    public void DefaultBufferIsBoundedToEightMebibytes()
    {
        Assert.Equal(8 * 1024 * 1024, Restream.StreamBufferSize);
    }

    [Fact]
    public async Task OpenIsIdempotentAndNaturalEndDisposesTransport()
    {
        TrackingMemoryStream upstream = new([1, 2, 3, 4]);
        using SequenceHandler handler = new(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(upstream),
            });
        using ProviderHttpClient providerHttpClient = CreateProviderClient(handler);
        using Restream restream = CreateRestream(providerHttpClient);

        await restream.Open(CancellationToken.None);
        await restream.Open(CancellationToken.None);
        await using Stream reader = restream.GetStream();
        byte[] destination = new byte[8];

        Assert.Equal(4, await reader.ReadAsync(destination));
        Assert.Equal([1, 2, 3, 4], destination[..4]);
        Assert.Equal(0, await reader.ReadAsync(destination));
        Assert.Single(handler.RequestUris);
        Assert.True(upstream.IsDisposed);
        Assert.False(restream.EnableStreamSharing);

        Task firstClose = restream.Close();
        Task secondClose = restream.Close();
        Assert.Same(firstClose, secondClose);
        await firstClose;
    }

    [Fact]
    public async Task CloseUnblocksPendingReaderAndCancelsUpstreamCopy()
    {
        BlockingReadStream upstream = new();
        using SequenceHandler handler = new(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(upstream),
            });
        using ProviderHttpClient providerHttpClient = CreateProviderClient(handler);
        using Restream restream = CreateRestream(providerHttpClient);
        await restream.Open(CancellationToken.None);
        await using Stream reader = restream.GetStream();
        ValueTask<int> pendingRead = reader.ReadAsync(new byte[1]);

        await restream.Close().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(0, await pendingRead.AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.True(upstream.IsDisposed);
        Assert.False(restream.EnableStreamSharing);
        Assert.Throws<InvalidOperationException>(() => restream.GetStream());
    }

    [Fact]
    public async Task UpstreamFailureIsPropagatedAfterBufferedData()
    {
        FaultingReadStream upstream = new([10, 11, 12], new IOException("provider disconnected"));
        using SequenceHandler handler = new(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(upstream),
            });
        using ProviderHttpClient providerHttpClient = CreateProviderClient(handler);
        using Restream restream = CreateRestream(providerHttpClient);

        await restream.Open(CancellationToken.None);
        await using Stream reader = restream.GetStream();
        byte[] destination = new byte[3];

        Assert.Equal(3, await reader.ReadAsync(destination));
        IOException exception = await Assert.ThrowsAsync<IOException>(
            async () => await reader.ReadExactlyAsync(destination));
        Assert.Equal("provider disconnected", exception.Message);
        Assert.False(restream.EnableStreamSharing);
        Assert.True(upstream.IsDisposed);
    }

    [Fact]
    public async Task FailedOpenDisposesResponseAndCannotCreateReader()
    {
        TrackingMemoryStream upstream = new([1]);
        using SequenceHandler handler = new(
            _ => new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StreamContent(upstream),
            });
        using ProviderHttpClient providerHttpClient = CreateProviderClient(handler);
        using Restream restream = CreateRestream(providerHttpClient);

        await Assert.ThrowsAsync<HttpRequestException>(() => restream.Open(CancellationToken.None));

        Assert.True(upstream.IsDisposed);
        Assert.False(restream.EnableStreamSharing);
        Assert.Throws<InvalidOperationException>(() => restream.GetStream());
        await restream.Close();
    }

    [Fact]
    public async Task TemporaryRelativeRedirectIsFollowedWithoutDuplicatingOpen()
    {
        using SequenceHandler handler = new(
            _ =>
            {
                HttpResponseMessage response = new(HttpStatusCode.TemporaryRedirect);
                response.Headers.Location = new Uri("/redirected", UriKind.Relative);
                return response;
            },
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([99]),
            });
        using ProviderHttpClient providerHttpClient = CreateProviderClient(handler);
        using Restream restream = CreateRestream(providerHttpClient, userAgent: "Xtream-Test/1.0");

        await restream.Open(CancellationToken.None);
        await using Stream reader = restream.GetStream();
        byte[] destination = new byte[1];

        Assert.Equal(1, await reader.ReadAsync(destination));
        Assert.Equal(99, destination[0]);
        Assert.Equal(0, await reader.ReadAsync(destination));
        Assert.Equal(
            ["https://provider.example/live/user/password/1.ts", "https://provider.example/redirected"],
            handler.RequestUris);
        Assert.All(handler.UserAgents, value => Assert.Equal("Xtream-Test/1.0", value));
    }

    [Fact]
    public async Task CrossOriginRedirectIsRejectedWithoutContactingTarget()
    {
        using SequenceHandler handler = new(
            _ =>
            {
                HttpResponseMessage response = new(HttpStatusCode.Redirect);
                response.Headers.Location = new Uri("http://127.0.0.1/latest/meta-data/");
                return response;
            });
        using ProviderHttpClient providerHttpClient = CreateProviderClient(handler);
        using Restream restream = CreateRestream(providerHttpClient);

        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => restream.Open(CancellationToken.None));

        Assert.Contains("configured provider origin", exception.Message, StringComparison.Ordinal);
        Assert.Single(handler.RequestUris);
    }

    [Fact]
    public async Task PublicProviderHostnameResolvingToPrivateAddressIsRejected()
    {
        using SequenceHandler handler = new(
            _ =>
            {
                HttpResponseMessage response = new(HttpStatusCode.TemporaryRedirect);
                response.Headers.Location = new Uri("/redirected", UriKind.Relative);
                return response;
            });
        using ProviderHttpClient providerHttpClient = CreateProviderClient(handler, IPAddress.Loopback);
        using Restream restream = CreateRestream(providerHttpClient);

        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => restream.Open(CancellationToken.None));

        Assert.Contains("resolved only to private or local", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.RequestUris);
    }

    [Fact]
    public void GetStreamBeforeOpenThrowsInsteadOfStartingBackgroundWork()
    {
        using SequenceHandler handler = new(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([]),
            });
        using ProviderHttpClient providerHttpClient = CreateProviderClient(handler);
        using Restream restream = CreateRestream(providerHttpClient);

        Assert.Throws<InvalidOperationException>(() => restream.GetStream());
        Assert.Empty(handler.RequestUris);
    }

    private static Restream CreateRestream(ProviderHttpClient providerHttpClient, string? userAgent = null)
    {
        MediaSourceInfo mediaSource = new()
        {
            Id = "channel-1",
            Path = "https://provider.example/live/user/password/1.ts",
        };
        return new Restream(
            providerHttpClient,
            NullLogger.Instance,
            mediaSource,
            path => "https://jellyfin.example" + path,
            path => "http://127.0.0.1:8096" + path,
            4096,
            userAgent,
            new Uri("https://provider.example/"));
    }

    private static ProviderHttpClient CreateProviderClient(HttpMessageHandler handler, params IPAddress[] addresses)
    {
        IPAddress[] resolved = addresses.Length == 0 ? [IPAddress.Parse("93.184.216.34")] : addresses;
        return new ProviderHttpClient(handler, (_, _) => Task.FromResult(resolved));
    }

    private sealed class SequenceHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private int _requestIndex;

        public List<string> RequestUris { get; } = [];

        public List<string> UserAgents { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int index = Interlocked.Increment(ref _requestIndex) - 1;
            if (index >= responses.Length)
            {
                throw new InvalidOperationException("The test handler received more requests than expected.");
            }

            RequestUris.Add(request.RequestUri?.ToString() ?? string.Empty);
            UserAgents.Add(request.Headers.UserAgent.ToString());
            HttpResponseMessage response = responses[index](request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private sealed class TrackingMemoryStream(byte[] buffer) : MemoryStream(buffer)
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        public bool IsDisposed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WaitForCancellationAsync(cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => new(WaitForCancellationAsync(cancellationToken));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }

        private static async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class FaultingReadStream(byte[] content, Exception failure) : Stream
    {
        private bool _contentReturned;

        public bool IsDisposed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadCore(buffer.AsSpan(offset, count));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return Task.FromResult(ReadCore(buffer.AsSpan(offset, count)));
            }
            catch (Exception ex)
            {
                return Task.FromException<int>(ex);
            }
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return ValueTask.FromResult(ReadCore(buffer.Span));
            }
            catch (Exception ex)
            {
                return ValueTask.FromException<int>(ex);
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }

        private int ReadCore(Span<byte> destination)
        {
            if (_contentReturned)
            {
                throw failure;
            }

            _contentReturned = true;
            content.CopyTo(destination);
            return content.Length;
        }
    }
}
