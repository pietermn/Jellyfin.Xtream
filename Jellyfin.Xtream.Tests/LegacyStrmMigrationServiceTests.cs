using Jellyfin.Xtream.Service;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Xtream.Tests;

public sealed class LegacyStrmMigrationServiceTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "jellyfin-xtream-legacy-tests-" + Guid.NewGuid().ToString("N"));
    private readonly LegacyStrmMigrationService _service = new(
        NullLogger<LegacyStrmMigrationService>.Instance);
    [Fact]
    public async Task PreviewFindsOnlyExactLegacyVodOwnershipSignature()
    {
        await WriteAsync(
            "Movie/Movie [42].mkv.strm",
            "https://provider.example:8443/panel/movie/account/secret/42.mkv\n");
        await WriteAsync(
            "Manual/Manual [43].mkv.strm",
            "https://unrelated.example/media/43.mkv\n");
        await WriteAsync(
            "Custom Name.strm",
            "https://provider.example:8443/panel/movie/account/secret/44.mkv\n");
        await WriteAsync(
            "Manual Pattern/Manual Pattern [44].mkv.strm",
            "https://provider.example:8443/panel/movie/only-one-segment/44.mkv\n");
        await WriteAsync(
            "Movie [xtream-vod-45]/Movie [xtream-vod-45].mkv.strm",
            "https://provider.example:8443/panel/movie/account/secret/45.mkv\n");

        LegacyStrmMigrationPreview preview = await _service.PreviewRootAsync(
            _rootPath,
            LegacyStrmExportKind.Vod,
            CancellationToken.None);

        LegacyStrmMigrationCandidate candidate = Assert.Single(preview.Candidates);
        Assert.Equal("Movie/Movie [42].mkv.strm", candidate.RelativePath);
        Assert.Equal(42, candidate.StreamId);
        Assert.False(preview.Truncated);
    }

    [Fact]
    public async Task PreviewIsIncompleteWhenAStrmCannotBeSafelyInspected()
    {
        await WriteAsync(
            "Oversized/Oversized [42].mkv.strm",
            new string('x', 4097));

        LegacyStrmMigrationPreview preview = await _service.PreviewRootAsync(
            _rootPath,
            LegacyStrmExportKind.Vod,
            CancellationToken.None);

        Assert.True(preview.Incomplete);
        Assert.Equal(1, preview.SkippedPathCount);
        Assert.Empty(preview.Candidates);
    }

    [Fact]
    public async Task PreviewRequiresSeriesSeasonAndUrlIdsToAgree()
    {
        await WriteAsync(
            "Show/Season 102/S102E103 - Episode [77].strm",
            "https://provider.example:8443/panel//series/account/secret/77.mp4\n");
        await WriteAsync(
            "Show/Season 03/S02E04 - Wrong season [78].strm",
            "https://provider.example:8443/panel/series/account/secret/78.mp4\n");
        await WriteAsync(
            "Show/Season 02/S02E05 - Wrong id [79].strm",
            "https://provider.example:8443/panel/series/account/secret/80.mp4\n");

        LegacyStrmMigrationPreview preview = await _service.PreviewRootAsync(
            _rootPath,
            LegacyStrmExportKind.Series,
            CancellationToken.None);

        LegacyStrmMigrationCandidate candidate = Assert.Single(preview.Candidates);
        Assert.Equal(77, candidate.StreamId);
    }

    [Fact]
    public async Task PreviewFindsPreIdLayoutsAndCredentialsFromAnOldProvider()
    {
        await WriteAsync(
            "Classic Movie/Classic Movie.mp4.strm",
            "http://retired-provider.example/movie/old-user/old-password/91.mp4\n");
        await WriteAsync(
            "Classic Show/Season 101/Classic Show - S101E102 - Pilot.strm",
            "http://retired-provider.example/series/old-user/old-password/92.mkv\n");

        LegacyStrmMigrationPreview vod = await _service.PreviewRootAsync(
            _rootPath,
            LegacyStrmExportKind.Vod,
            CancellationToken.None);
        LegacyStrmMigrationPreview series = await _service.PreviewRootAsync(
            _rootPath,
            LegacyStrmExportKind.Series,
            CancellationToken.None);

        Assert.Equal(91, Assert.Single(vod.Candidates).StreamId);
        Assert.Equal(92, Assert.Single(series.Candidates).StreamId);
    }

    [Fact]
    public async Task QuarantineSkipsAFileChangedAfterPreview()
    {
        const string relativePath = "Movie/Movie [42].mkv.strm";
        await WriteAsync(
            relativePath,
            "https://old.example/movie/account/old-secret/42.mkv\n");
        LegacyStrmMigrationPreview preview = await _service.PreviewRootAsync(
            _rootPath,
            LegacyStrmExportKind.Vod,
            CancellationToken.None);
        await WriteAsync(
            relativePath,
            "https://different.example/movie/account/different-secret/42.mkv\n");

        LegacyStrmMigrationResult result = await _service.QuarantinePreviewAsync(
            preview,
            CancellationToken.None);

        Assert.Equal(0, result.QuarantinedCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.True(File.Exists(Path.Combine(_rootPath, relativePath)));
    }

    [Fact]
    public async Task QuarantineRejectsAPrecreatedQuarantineSymlink()
    {
        const string relativePath = "Movie/Movie [42].mkv.strm";
        await WriteAsync(
            relativePath,
            "https://old.example/movie/account/old-secret/42.mkv\n");
        LegacyStrmMigrationPreview preview = await _service.PreviewRootAsync(
            _rootPath,
            LegacyStrmExportKind.Vod,
            CancellationToken.None);
        string outsidePath = Path.Combine(
            Path.GetTempPath(),
            "jellyfin-xtream-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsidePath);
        try
        {
            Directory.CreateSymbolicLink(
                Path.Combine(_rootPath, ".jellyfin-xtream-legacy-quarantine"),
                outsidePath);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _service.QuarantinePreviewAsync(preview, CancellationToken.None));

            Assert.True(File.Exists(Path.Combine(_rootPath, relativePath)));
            Assert.Empty(Directory.EnumerateFileSystemEntries(outsidePath));
        }
        finally
        {
            Directory.Delete(outsidePath, true);
        }
    }

    [Fact]
    public async Task PreviewReportsAMissingRootInsteadOfClaimingItIsClean()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            _service.PreviewRootAsync(
                _rootPath,
                LegacyStrmExportKind.Vod,
                CancellationToken.None));
    }

    [Fact]
    public async Task QuarantineMovesCandidatesOutOfLibraryDiscoveryWithoutDeletingManualFiles()
    {
        const string legacyRelativePath = "Movie/Movie [42].mkv.strm";
        const string manualRelativePath = "Manual/Manual [43].mkv.strm";
        await WriteAsync(
            legacyRelativePath,
            "https://provider.example:8443/panel/movie/account/secret/42.mkv\n");
        await WriteAsync(manualRelativePath, "https://media.example/manual.mkv\n");
        LegacyStrmMigrationPreview preview = await _service.PreviewRootAsync(
            _rootPath,
            LegacyStrmExportKind.Vod,
            CancellationToken.None);

        LegacyStrmMigrationResult result = await _service.QuarantinePreviewAsync(
            preview,
            CancellationToken.None);

        Assert.Equal(1, result.QuarantinedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.False(File.Exists(Path.Combine(_rootPath, legacyRelativePath)));
        Assert.True(File.Exists(Path.Combine(_rootPath, manualRelativePath)));
        Assert.NotNull(result.QuarantinePath);
        Assert.True(File.Exists(Path.Combine(result.QuarantinePath, legacyRelativePath + ".quarantined")));
        string reportPath = Path.Combine(result.QuarantinePath, "migration-report.json");
        Assert.True(File.Exists(reportPath));
        string report = await File.ReadAllTextAsync(reportPath);
        Assert.Contains("\"status\": \"complete\"", report, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", report, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(result.QuarantinePath, "*.strm", SearchOption.AllDirectories));
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, true);
        }
    }

    private async Task WriteAsync(string relativePath, string content)
    {
        string path = Path.Combine(_rootPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }
}
