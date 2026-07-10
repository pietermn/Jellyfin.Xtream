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
        using HttpClient httpClient = new(handler);
        using XtreamClient client = new(httpClient, NullLogger<XtreamClient>.Instance);

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
}
