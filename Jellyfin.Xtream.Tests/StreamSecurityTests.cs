using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Service;

namespace Jellyfin.Xtream.Tests;

public class StreamSecurityTests
{
    [Fact]
    public void UpstreamPathCredentialsAreEscaped()
    {
        ConnectionInfo connection = new("https://provider.example", "name with/slash", "p@ss/word");

        Uri uri = StreamUriBuilder.Build(connection, StreamType.Vod, 12, ".mkv");

        Assert.Contains("name%20with%2Fslash", uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("p%40ss%2Fword", uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.EndsWith("/12.mkv", uri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void ProxySignatureIsBoundToOneStream()
    {
        ConnectionInfo connection = new("https://provider.example", "user", "password");
        string signature = StreamProxySigner.Sign(connection, StreamType.Series, 42, "mkv", null, 0);

        Assert.True(StreamProxySigner.Verify(connection, StreamType.Series, 42, "mkv", null, 0, signature));
        Assert.False(StreamProxySigner.Verify(connection, StreamType.Series, 43, "mkv", null, 0, signature));
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
