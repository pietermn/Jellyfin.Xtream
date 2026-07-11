using System.Net;
using Jellyfin.Xtream.Client;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Xtream.Tests;

public class XtreamClientTests
{
    [Fact]
    public async Task CredentialsAreEncodedInApiRequest()
    {
        RecordingHandler handler = new("{\"user_info\":{},\"server_info\":{}}");
        using ProviderHttpClient providerHttpClient = CreateProviderClient(handler);
        using XtreamClient client = new(providerHttpClient, NullLogger<XtreamClient>.Instance);

        await client.GetUserAndServerInfoAsync(
            new ConnectionInfo("https://provider.example/", "name+with space", "p&ss?word"),
            CancellationToken.None);

        Assert.NotNull(handler.RequestUri);
        Assert.Contains("username=name%2Bwith%20space", handler.RequestUri.Query, StringComparison.Ordinal);
        Assert.Contains("password=p%26ss%3Fword", handler.RequestUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectionInfoNeverPrintsCredentials()
    {
        ConnectionInfo connection = new("https://provider.example", "secret-user", "secret-password");

        string value = connection.ToString();

        Assert.DoesNotContain("secret-user", value, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-password", value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationInterruptsStalledJsonResponseBody()
    {
        AsyncOnlyBlockingStream responseBody = new();
        using ResponseStreamHandler handler = new(responseBody);
        using ProviderHttpClient providerHttpClient = CreateProviderClient(handler);
        using XtreamClient client = new(providerHttpClient, NullLogger<XtreamClient>.Instance);
        using CancellationTokenSource cancellationTokenSource = new();

        Task request = client.GetUserAndServerInfoAsync(
            new ConnectionInfo("https://provider.example/", "user", "password"),
            cancellationTokenSource.Token);
        await responseBody.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(0, responseBody.SynchronousReadCalls);
        Assert.True(responseBody.IsDisposed);
    }

    private static ProviderHttpClient CreateProviderClient(HttpMessageHandler handler)
        => new(handler, (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

    private sealed class RecordingHandler(string json) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            });
        }
    }

    private sealed class ResponseStreamHandler(Stream responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(responseBody),
            });
        }
    }

    private sealed class AsyncOnlyBlockingStream : Stream
    {
        private int _synchronousReadCalls;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public bool IsDisposed { get; private set; }

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

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
