using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Service;

namespace Jellyfin.Xtream.Tests;

public sealed class StreamProxyTokenServiceTests : IDisposable
{
    private const string ConfigurationFingerprint = "test-selection-fingerprint";

    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "jellyfin-xtream-token-tests-" + Guid.NewGuid().ToString("N"));
    private readonly MutableTimeProvider _timeProvider = new(
        new DateTimeOffset(2026, 7, 11, 10, 0, 0, TimeSpan.Zero));

    [Fact]
    public void PlaybackGrantExpiresAfterFifteenMinutes()
    {
        StreamProxyTokenService service = CreateService();
        ConnectionInfo connection = CreateConnection();
        StreamProxyGrant grant = service.CreatePlaybackGrant(
            connection,
            ConfigurationFingerprint,
            StreamType.Vod,
            42,
            "mkv",
            null,
            0);

        Assert.True(VerifyPlayback(service, connection, grant));

        _timeProvider.Advance(TimeSpan.FromMinutes(15));

        Assert.False(VerifyPlayback(service, connection, grant));
    }

    [Fact]
    public void PersistentGrantSurvivesRestartWithoutStoringProviderCredentials()
    {
        ConnectionInfo connection = CreateConnection();
        StreamProxyTokenService first = CreateService();
        StreamProxyGrant grant = first.CreatePersistentStrmGrant(
            connection,
            ConfigurationFingerprint,
            StreamType.Series,
            100,
            "mp4",
            null,
            0);

        StreamProxyTokenService restarted = CreateService();

        Assert.True(restarted.VerifyPersistentStrmGrant(
            connection,
            ConfigurationFingerprint,
            StreamType.Series,
            100,
            "mp4",
            null,
            0,
            grant.KeyId,
            grant.Signature));
        string persistedKeyRing = File.ReadAllText(GetKeyRingPath());
        Assert.DoesNotContain(connection.BaseUrl, persistedKeyRing, StringComparison.Ordinal);
        Assert.DoesNotContain(connection.UserName, persistedKeyRing, StringComparison.Ordinal);
        Assert.DoesNotContain(connection.Password, persistedKeyRing, StringComparison.Ordinal);
    }

    [Fact]
    public void GrantIsBoundToPurposeConfigurationAndStream()
    {
        StreamProxyTokenService service = CreateService();
        ConnectionInfo connection = CreateConnection();
        StreamProxyGrant grant = service.CreatePersistentStrmGrant(
            connection,
            ConfigurationFingerprint,
            StreamType.Vod,
            42,
            "mkv",
            null,
            0);

        Assert.False(service.VerifyPersistentStrmGrant(
            new ConnectionInfo(connection.BaseUrl, connection.UserName, "changed-password"),
            ConfigurationFingerprint,
            StreamType.Vod,
            42,
            "mkv",
            null,
            0,
            grant.KeyId,
            grant.Signature));
        Assert.False(service.VerifyPersistentStrmGrant(
            connection,
            "changed-selection-fingerprint",
            StreamType.Vod,
            42,
            "mkv",
            null,
            0,
            grant.KeyId,
            grant.Signature));
        Assert.False(service.VerifyPersistentStrmGrant(
            connection,
            ConfigurationFingerprint,
            StreamType.Vod,
            43,
            "mkv",
            null,
            0,
            grant.KeyId,
            grant.Signature));
        Assert.False(service.VerifyPlaybackGrant(
            connection,
            ConfigurationFingerprint,
            StreamType.Vod,
            42,
            "mkv",
            null,
            0,
            grant.KeyId,
            _timeProvider.GetUtcNow().AddMinutes(15).ToUnixTimeSeconds(),
            grant.Signature));
    }

    [Fact]
    public void KeysRotateIndependentlyAndRevokeTheirOwnGrants()
    {
        StreamProxyTokenService service = CreateService();
        ConnectionInfo connection = CreateConnection();
        StreamProxyGrant playback = service.CreatePlaybackGrant(
            connection,
            ConfigurationFingerprint,
            StreamType.Vod,
            42,
            "mkv",
            null,
            0);
        StreamProxyGrant persistent = service.CreatePersistentStrmGrant(
            connection,
            ConfigurationFingerprint,
            StreamType.Vod,
            42,
            "mkv",
            null,
            0);

        service.RotatePlaybackKey();

        Assert.False(VerifyPlayback(service, connection, playback));
        Assert.True(VerifyPersistent(service, connection, persistent));

        service.RotatePersistentStrmKey();

        Assert.False(VerifyPersistent(service, connection, persistent));
    }

    [Fact]
    public void MalformedGrantValuesFailClosed()
    {
        StreamProxyTokenService service = CreateService();
        ConnectionInfo connection = CreateConnection();
        StreamProxyGrant grant = service.CreatePlaybackGrant(
            connection,
            ConfigurationFingerprint,
            StreamType.Vod,
            42,
            "mkv",
            null,
            0);

        Assert.False(service.VerifyPlaybackGrant(
            connection,
            ConfigurationFingerprint,
            StreamType.Vod,
            42,
            "../mkv",
            null,
            0,
            grant.KeyId,
            grant.ExpiresAtUnixSeconds!.Value,
            grant.Signature));
        Assert.False(service.VerifyPlaybackGrant(
            connection,
            ConfigurationFingerprint,
            StreamType.Vod,
            42,
            "mkv",
            null,
            0,
            grant.KeyId,
            grant.ExpiresAtUnixSeconds.Value,
            "not-base64!"));
    }

    [Fact]
    public void SelectionFingerprintIsStableAndChangesWithAuthorizationScope()
    {
        PluginConfiguration first = new();
        first.IsVodStrmExportEnabled = true;
        first.VodStrmExportPath = Path.Combine(_rootPath, "vod[scope]");
        first.Vod[2] = [30, 10];
        first.Series[7] = [];
        PluginConfiguration reordered = new();
        reordered.IsVodStrmExportEnabled = true;
        reordered.VodStrmExportPath = Path.Combine(_rootPath, "vod[scope]", ".");
        reordered.Series[7] = [];
        reordered.Vod[2] = [10, 30];

        string original = StreamProxyConfigurationFingerprint.Create(first);

        Assert.Equal(original, StreamProxyConfigurationFingerprint.Create(reordered));

        reordered.Vod[2].Add(50);

        Assert.NotEqual(original, StreamProxyConfigurationFingerprint.Create(reordered));
    }

    [Fact]
    public void ExportStateAndNormalizedRootsRevokePersistentConfigurationBinding()
    {
        PluginConfiguration configuration = new()
        {
            IsVodStrmExportEnabled = true,
            VodStrmExportPath = Path.Combine(_rootPath, "vod"),
            IsSeriesStrmExportEnabled = true,
            SeriesStrmExportPath = Path.Combine(_rootPath, "series"),
        };
        string original = StreamProxyConfigurationFingerprint.Create(configuration);

        configuration.IsVodStrmExportEnabled = false;
        Assert.NotEqual(original, StreamProxyConfigurationFingerprint.Create(configuration));

        configuration.IsVodStrmExportEnabled = true;
        configuration.VodStrmExportPath = Path.Combine(_rootPath, "moved-vod");
        Assert.NotEqual(original, StreamProxyConfigurationFingerprint.Create(configuration));

        configuration.VodStrmExportPath = Path.Combine(_rootPath, "vod");
        configuration.PublicServerUrl = "https://media.example/jellyfin";
        Assert.NotEqual(original, StreamProxyConfigurationFingerprint.Create(configuration));
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    private static ConnectionInfo CreateConnection()
    {
        return new("https://provider.example:8443", "secret-user", "secret-password");
    }

    private static bool VerifyPersistent(
        StreamProxyTokenService service,
        ConnectionInfo connection,
        StreamProxyGrant grant)
    {
        return service.VerifyPersistentStrmGrant(
            connection,
            ConfigurationFingerprint,
            StreamType.Vod,
            42,
            "mkv",
            null,
            0,
            grant.KeyId,
            grant.Signature);
    }

    private static bool VerifyPlayback(
        StreamProxyTokenService service,
        ConnectionInfo connection,
        StreamProxyGrant grant)
    {
        return service.VerifyPlaybackGrant(
            connection,
            ConfigurationFingerprint,
            StreamType.Vod,
            42,
            "mkv",
            null,
            0,
            grant.KeyId,
            grant.ExpiresAtUnixSeconds!.Value,
            grant.Signature);
    }

    private StreamProxyTokenService CreateService()
    {
        return new(GetKeyRingPath(), _timeProvider);
    }

    private string GetKeyRingPath()
    {
        return Path.Combine(_rootPath, "proxy-keys.json");
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan value)
        {
            _utcNow = _utcNow.Add(value);
        }
    }
}
