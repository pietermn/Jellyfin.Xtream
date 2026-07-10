using Jellyfin.Xtream.Service;

namespace Jellyfin.Xtream.Tests;

public class StrmExportPathPolicyTests
{
    [Fact]
    public void VodPathsWithSameTitleUseStableProviderIdentity()
    {
        string first = StrmExportPathPolicy.BuildVodRelativePath("Dune", 101, "mkv");
        string second = StrmExportPathPolicy.BuildVodRelativePath("Dune", 202, "mkv");

        Assert.NotEqual(first, second);
        Assert.Contains("xtream-vod-101", first, StringComparison.Ordinal);
        Assert.Contains("xtream-vod-202", second, StringComparison.Ordinal);
        Assert.EndsWith(".mkv.strm", first, StringComparison.Ordinal);
    }

    [Fact]
    public void SeriesPathsWithSameTitleUseStableProviderIdentity()
    {
        string first = StrmExportPathPolicy.BuildSeriesRelativeDirectory("The Office", 10);
        string second = StrmExportPathPolicy.BuildSeriesRelativeDirectory("The Office", 20);

        Assert.NotEqual(first, second);
        Assert.Contains("xtream-series-10", first, StringComparison.Ordinal);
        Assert.Contains("xtream-series-20", second, StringComparison.Ordinal);
    }

    [Fact]
    public void OverlappingRootsAreDetectedCasePortably()
    {
        string parent = Path.Combine(Path.GetTempPath(), "Jellyfin-Xtream", "Movies");
        string nestedWithDifferentCase = Path.Combine(Path.GetTempPath(), "jellyfin-xtream", "movies", "Series");

        Assert.True(StrmExportPathPolicy.RootsOverlap(parent, parent));
        Assert.True(StrmExportPathPolicy.RootsOverlap(parent, nestedWithDifferentCase));
        Assert.False(StrmExportPathPolicy.RootsOverlap(
            parent,
            Path.Combine(Path.GetTempPath(), "Jellyfin-Xtream", "Movies-Archive")));
    }

    [Theory]
    [InlineData("../outside.strm")]
    [InlineData("folder/../../outside.strm")]
    [InlineData("folder\\..\\outside.strm")]
    [InlineData("not-a-stream.txt")]
    public void ManagedPathResolutionRejectsUnsafePaths(string relativePath)
    {
        string root = Path.Combine(Path.GetTempPath(), "jellyfin-xtream-path-tests");

        Assert.False(StrmExportPathPolicy.TryResolveManagedStrmPath(root, relativePath, out _));
    }
}
