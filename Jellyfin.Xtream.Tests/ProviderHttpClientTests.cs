using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Jellyfin.Xtream.Client;

namespace Jellyfin.Xtream.Tests;

public class ProviderHttpClientTests
{
    private static readonly IPAddress _publicAddress = IPAddress.Parse("93.184.216.34");

    [Fact]
    public void DedicatedHandlerDisablesRedirectsAndCredentialBearingDiagnostics()
    {
        using SocketsHttpHandler handler = ProviderHttpClient.CreatePrimaryHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.Null(handler.ActivityHeadersPropagator);
        Assert.NotNull(handler.ConnectCallback);
        Assert.False(handler.UseProxy);
    }

    [Fact]
    public async Task CredentialBearingRequestDoesNotReachHttpDiagnosticListener()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using CancellationTokenSource timeoutTokenSource = new(TimeSpan.FromSeconds(5));
            Task<string> server = ServeSingleRequestAsync(listener, timeoutTokenSource.Token);
            using HttpDiagnosticCollector diagnostics = new();
            using ProviderHttpClient client = new();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Uri providerBaseUri = new($"http://127.0.0.1:{port}/");
            using HttpRequestMessage request = new(
                HttpMethod.Get,
                new Uri(providerBaseUri, "live/diagnostic-secret-user/diagnostic-secret-password/1.ts"));

            using HttpResponseMessage response = await client.SendAsync(
                request,
                providerBaseUri,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutTokenSource.Token);
            string rawRequest = await server;

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("diagnostic-secret-user/diagnostic-secret-password", rawRequest, StringComparison.Ordinal);
            Assert.Equal(0, diagnostics.CredentialRequestEvents);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task RelativeSameOriginRedirectPreservesRequestHeaders()
    {
        using SequenceHandler handler = new(
            _ => Redirect(HttpStatusCode.TemporaryRedirect, "/edge/stream.ts"),
            _ => new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent([42]),
            });
        using ProviderHttpClient client = CreateClient(handler, _publicAddress);
        using HttpRequestMessage request = new(HttpMethod.Get, "https://provider.example/live/user/password/1.ts");
        request.Headers.TryAddWithoutValidation("Range", "bytes=10-");
        request.Headers.TryAddWithoutValidation("User-Agent", "Xtream-Test/1.0");

        using HttpResponseMessage response = await client.SendAsync(
            request,
            new Uri("https://provider.example/"),
            HttpCompletionOption.ResponseHeadersRead,
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(
            ["https://provider.example/live/user/password/1.ts", "https://provider.example/edge/stream.ts"],
            handler.RequestUris);
        Assert.All(handler.RangeHeaders, value => Assert.Equal("bytes=10-", value));
        Assert.All(handler.UserAgents, value => Assert.Equal("Xtream-Test/1.0", value));
    }

    [Theory]
    [InlineData("http://provider.example/redirected")]
    [InlineData("https://other.example/redirected")]
    [InlineData("https://provider.example:444/redirected")]
    [InlineData("ftp://provider.example/redirected")]
    public async Task UnsafeOriginRedirectIsRejectedBeforeTargetRequest(string location)
    {
        using SequenceHandler handler = new(_ => Redirect(HttpStatusCode.Redirect, location));
        using ProviderHttpClient client = CreateClient(handler, _publicAddress);
        using HttpRequestMessage request = new(HttpMethod.Get, "https://provider.example/player_api.php?username=user&password=secret");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.SendAsync(
                request,
                new Uri("https://provider.example/"),
                HttpCompletionOption.ResponseHeadersRead,
                CancellationToken.None));

        Assert.Single(handler.RequestUris);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.20.30.40")]
    [InlineData("100.100.100.200")]
    [InlineData("169.254.169.254")]
    [InlineData("172.20.1.2")]
    [InlineData("192.168.1.2")]
    [InlineData("192.0.0.1")]
    [InlineData("198.18.0.1")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("64:ff9b::a9fe:a9fe")]
    public void PrivateAndLocalAddressesAreRejected(string address)
    {
        Assert.False(ProviderRedirectPolicy.IsPublicAddress(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task PublicHostnameResolvingOnlyToPrivateAddressIsRejectedBeforeRequest()
    {
        using SequenceHandler handler = new(_ => Redirect(HttpStatusCode.Redirect, "/redirected"));
        using ProviderHttpClient client = CreateClient(handler, IPAddress.Loopback);
        using HttpRequestMessage request = new(HttpMethod.Get, "https://provider.example/live/user/password/1.ts");

        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.SendAsync(
                request,
                new Uri("https://provider.example/"),
                HttpCompletionOption.ResponseHeadersRead,
                CancellationToken.None));

        Assert.Contains("resolved only to private or local", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.RequestUris);
    }

    [Fact]
    public async Task MetadataHostnameRedirectIsRejectedWithoutDnsLookup()
    {
        bool resolverCalled = false;
        using SequenceHandler handler = new(_ => Redirect(HttpStatusCode.Redirect, "/latest/meta-data"));
        using ProviderHttpClient client = new(
            handler,
            (_, _) =>
            {
                resolverCalled = true;
                return Task.FromResult(new[] { _publicAddress });
            });
        using HttpRequestMessage request = new(HttpMethod.Get, "http://metadata.google.internal/provider");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.SendAsync(
                request,
                new Uri("http://metadata.google.internal/"),
                HttpCompletionOption.ResponseHeadersRead,
                CancellationToken.None));

        Assert.False(resolverCalled);
        Assert.Empty(handler.RequestUris);
    }

    [Fact]
    public async Task ApprovedAddressIsPinnedAcrossLaterDnsChanges()
    {
        int resolverCalls = 0;
        using SequenceHandler handler = new(
            _ => new HttpResponseMessage(HttpStatusCode.OK),
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        using ProviderHttpClient client = new(
            handler,
            (_, _) => Task.FromResult(
                Interlocked.Increment(ref resolverCalls) == 1
                    ? new[] { _publicAddress }
                    : new[] { IPAddress.Loopback }));

        for (int id = 1; id <= 2; id++)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, $"https://provider.example/live/user/password/{id}.ts");
            using HttpResponseMessage response = await client.SendAsync(
                request,
                new Uri("https://provider.example/"),
                HttpCompletionOption.ResponseHeadersRead,
                CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(1, resolverCalls);
        Assert.Equal(2, handler.ApprovedAddresses.Count);
        Assert.All(handler.ApprovedAddresses, addresses => Assert.Equal(new[] { _publicAddress }, addresses));
    }

    [Fact]
    public async Task ExplicitPrivateProviderOriginCanFollowItsOwnRelativeRedirect()
    {
        using SequenceHandler handler = new(
            _ => Redirect(HttpStatusCode.TemporaryRedirect, "/redirected"),
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        using ProviderHttpClient client = new(
            handler,
            (_, _) => throw new InvalidOperationException("IP literals must not use DNS."));
        using HttpRequestMessage request = new(HttpMethod.Get, "http://192.168.1.20/live/user/password/1.ts");

        using HttpResponseMessage response = await client.SendAsync(
            request,
            new Uri("http://192.168.1.20/"),
            HttpCompletionOption.ResponseHeadersRead,
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.RequestUris.Count);
        Assert.All(handler.ApprovedAddresses, addresses => Assert.Equal(new[] { IPAddress.Parse("192.168.1.20") }, addresses));
    }

    [Fact]
    public async Task ExplicitHomeArpaProviderOriginCanUseAPrivateAddress()
    {
        using SequenceHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using ProviderHttpClient client = CreateClient(handler, IPAddress.Parse("192.168.1.20"));
        using HttpRequestMessage request = new(HttpMethod.Get, "http://iptv.home.arpa/live/user/password/1.ts");

        using HttpResponseMessage response = await client.SendAsync(
            request,
            new Uri("http://iptv.home.arpa/"),
            HttpCompletionOption.ResponseHeadersRead,
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([IPAddress.Parse("192.168.1.20")], Assert.Single(handler.ApprovedAddresses));
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://100.100.100.200/latest/meta-data/")]
    [InlineData("http://192.0.2.1/provider")]
    [InlineData("http://198.18.0.1/provider")]
    [InlineData("http://[64:ff9b::a9fe:a9fe]/latest/meta-data/")]
    public async Task UnsafeNonGlobalEndpointsAreRejectedBeforeRequest(string endpoint)
    {
        using SequenceHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using ProviderHttpClient client = new(handler);
        using HttpRequestMessage request = new(HttpMethod.Get, endpoint);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(
            request,
            new Uri(endpoint),
            HttpCompletionOption.ResponseHeadersRead,
            CancellationToken.None));

        Assert.Empty(handler.RequestUris);
    }

    private static ProviderHttpClient CreateClient(SequenceHandler handler, params IPAddress[] addresses)
        => new(handler, (_, _) => Task.FromResult(addresses));

    private static async Task<string> ServeSingleRequestAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using TcpClient connection = await listener.AcceptTcpClientAsync(cancellationToken);
        await using NetworkStream stream = connection.GetStream();
        using MemoryStream requestBuffer = new();
        byte[] buffer = new byte[1024];
        string requestText = string.Empty;
        while (requestBuffer.Length < 16 * 1024)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            requestBuffer.Write(buffer, 0, read);
            requestText = Encoding.ASCII.GetString(requestBuffer.GetBuffer(), 0, checked((int)requestBuffer.Length));
            if (requestText.Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                break;
            }
        }

        byte[] response = "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray();
        await stream.WriteAsync(response, cancellationToken);
        return requestText;
    }

    private static HttpResponseMessage Redirect(HttpStatusCode statusCode, string location)
    {
        HttpResponseMessage response = new(statusCode);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private sealed class SequenceHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private int _requestIndex;

        public List<string> RangeHeaders { get; } = [];

        public List<string> RequestUris { get; } = [];

        public List<string> UserAgents { get; } = [];

        public List<IPAddress[]> ApprovedAddresses { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int index = Interlocked.Increment(ref _requestIndex) - 1;
            if (index >= responses.Length)
            {
                throw new InvalidOperationException("The test handler received more requests than expected.");
            }

            RequestUris.Add(request.RequestUri?.ToString() ?? string.Empty);
            RangeHeaders.Add(request.Headers.TryGetValues("Range", out var ranges) ? string.Join(',', ranges) : string.Empty);
            UserAgents.Add(request.Headers.UserAgent.ToString());
            ApprovedAddresses.Add(
                ProviderHttpClient.TryGetApprovedAddresses(request, out IPAddress[]? addresses) && addresses is not null
                    ? addresses
                    : []);
            return Task.FromResult(responses[index](request));
        }
    }

    private sealed class HttpDiagnosticCollector :
        IObserver<DiagnosticListener>,
        IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly IDisposable _allListenersSubscription;
        private readonly List<IDisposable> _listenerSubscriptions = [];
        private int _credentialRequestEvents;

        public HttpDiagnosticCollector()
        {
            _allListenersSubscription = DiagnosticListener.AllListeners.Subscribe(this);
        }

        public int CredentialRequestEvents => Volatile.Read(ref _credentialRequestEvents);

        public void Dispose()
        {
            _allListenersSubscription.Dispose();
            lock (_listenerSubscriptions)
            {
                foreach (IDisposable subscription in _listenerSubscriptions)
                {
                    subscription.Dispose();
                }
            }
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(DiagnosticListener value)
        {
            if (!value.Name.Equals("HttpHandlerDiagnosticListener", StringComparison.Ordinal))
            {
                return;
            }

            IDisposable subscription = value.Subscribe(this);
            lock (_listenerSubscriptions)
            {
                _listenerSubscriptions.Add(subscription);
            }
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            object? payload = value.Value;
            if (payload?.GetType().GetProperty("Request")?.GetValue(payload) is HttpRequestMessage request
                && request.RequestUri?.AbsolutePath.Contains("diagnostic-secret", StringComparison.Ordinal) == true)
            {
                Interlocked.Increment(ref _credentialRequestEvents);
            }
        }
    }
}
