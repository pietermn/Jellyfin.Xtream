using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Api;
using Jellyfin.Xtream.Service;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using XtreamConnectionInfo = Jellyfin.Xtream.Client.ConnectionInfo;

namespace Jellyfin.Xtream.Tests;

public class StreamSecurityTests
{
    [Fact]
    public void ExplicitPublicServerUrlOverridesAndNormalizesAdvertisedUrl()
    {
        string result = PublicServerUrlPolicy.Resolve(
            " https://media.example/jellyfin/ ",
            "http://192.168.1.10:8096");

        Assert.Equal("https://media.example/jellyfin", result);
    }

    [Fact]
    public void EmptyPublicServerUrlUsesJellyfinAdvertisedUrl()
    {
        string result = PublicServerUrlPolicy.Resolve(
            string.Empty,
            "https://published.example/base/");

        Assert.Equal("https://published.example/base", result);
    }

    [Theory]
    [InlineData("ftp://media.example")]
    [InlineData("https://user:password@media.example")]
    [InlineData("https://media.example/path?token=value")]
    [InlineData("http://0.0.0.0:8096")]
    public void UnsafePublicServerUrlsAreRejected(string url)
    {
        Assert.Throws<ArgumentException>(() =>
            PublicServerUrlPolicy.Resolve(url, "https://fallback.example"));
    }

    [Fact]
    public void BearerProxyResponsesCannotBeCached()
    {
        DefaultHttpContext context = new();

        StreamProxyCachePolicy.Apply(context.Response);

        Assert.Equal("no-store, private", context.Response.Headers[HeaderNames.CacheControl]);
        Assert.Equal("no-cache", context.Response.Headers[HeaderNames.Pragma]);
        Assert.Equal("0", context.Response.Headers[HeaderNames.Expires]);
    }

    [Fact]
    public void UpstreamPathCredentialsAreEscaped()
    {
        XtreamConnectionInfo connection = new("https://provider.example", "name with/slash", "p@ss/word");

        Uri uri = StreamUriBuilder.Build(connection, StreamType.Vod, 12, ".mkv");

        Assert.Contains("name%20with%2Fslash", uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("p%40ss%2Fword", uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.EndsWith("/12.mkv", uri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../mkv")]
    [InlineData("mkv?token=x")]
    [InlineData("this-extension-is-too-long")]
    public void UnsafeExtensionsAreRejected(string extension)
    {
        Assert.Throws<ArgumentException>(() => StreamUriBuilder.NormalizeExtension(extension));
    }
}
