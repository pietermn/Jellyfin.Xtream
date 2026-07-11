using System.Diagnostics;
using Jellyfin.Xtream.Service;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Xtream.Tests;

public class NameNormalizationServiceTests
{
    [Fact]
    public void LegacyRuleAppliesToEveryScope()
    {
        NameNormalizationService service = CreateService();
        Assert.Empty(service.UpdateRules(@"^PREFIX\s*-\s* =>"));

        Assert.Equal("Title", service.Normalize("PREFIX - Title", NameScope.LiveChannel).Title);
        Assert.Equal("Title", service.Normalize("PREFIX - Title", NameScope.Vod).Title);
        Assert.Equal("Title", service.Normalize("PREFIX - Title", NameScope.Filesystem).Title);
    }

    [Fact]
    public void ScopedRuleOnlyAppliesToSelectedScope()
    {
        NameNormalizationService service = CreateService();
        Assert.Empty(service.UpdateRules(@"[LiveChannel] ^PREFIX\s*-\s* =>"));

        Assert.Equal("Title", service.Normalize("PREFIX - Title", NameScope.LiveChannel).Title);
        Assert.Equal("PREFIX - Title", service.Normalize("PREFIX - Title", NameScope.Vod).Title);
    }

    [Fact]
    public void ExportNamesApplyBothContentAndFilesystemScopesOnce()
    {
        NameNormalizationService service = CreateService();
        Assert.Empty(service.UpdateRules("[Vod] ^VOD\\s*=>\n[Filesystem] \\s*FS$ =>"));
        NameNormalizationSnapshot snapshot = service.CreateSnapshot();

        Assert.Equal(
            "Title",
            StrmExportService.NormalizeExportTitle(snapshot, "VOD Title FS", NameScope.Vod));
        Assert.Equal(
            "VOD Title",
            StrmExportService.NormalizeExportTitle(snapshot, "VOD Title FS", NameScope.Series));
    }

    [Fact]
    public void CharacterClassAtStartRemainsALegacyRegex()
    {
        NameNormalizationService service = CreateService();
        Assert.Empty(service.UpdateRules("[A-Z]+ => lower"));

        Assert.Equal("lower", service.Normalize("TITLE", NameScope.Series).Title);
    }

    [Fact]
    public void ScopeNamedCharacterClassRemainsALegacyRegexWithoutFollowingWhitespace()
    {
        NameNormalizationService service = CreateService();
        Assert.Empty(service.UpdateRules("[LiveChannel]+ => x"));

        Assert.Equal("x", service.Normalize("Live", NameScope.Vod).Title);
    }

    [Fact]
    public void InvalidScopeAndRegexAreReportedWhileValidRulesRemainActive()
    {
        NameNormalizationService service = CreateService();
        IReadOnlyList<string> errors = service.UpdateRules(
            "[LiveChannel,Missing] ^PREFIX =>\n(?<broken =>\n^GOOD\\s*-\\s* =>");

        Assert.Equal(2, errors.Count);
        Assert.Equal("Title", service.Normalize("GOOD - Title", NameScope.Vod).Title);
    }

    [Fact]
    public void SnapshotDoesNotChangeAfterRulesAreUpdated()
    {
        NameNormalizationService service = CreateService();
        Assert.Empty(service.UpdateRules("^OLD => New"));
        NameNormalizationSnapshot oldSnapshot = service.CreateSnapshot();

        Assert.Empty(service.UpdateRules("^OLD => Latest"));
        NameNormalizationSnapshot latestSnapshot = service.CreateSnapshot();

        Assert.Equal("New Title", oldSnapshot.Normalize("OLD Title", NameScope.Vod).Title);
        Assert.Equal("Latest Title", latestSnapshot.Normalize("OLD Title", NameScope.Vod).Title);
        Assert.NotEqual(oldSnapshot.Version, latestSnapshot.Version);
    }

    [Fact]
    public void ConservativeTagParsingPreservesSemanticSuffixes()
    {
        NameNormalizationService service = CreateService();

        ParsedName parsed = service.Normalize("[NL] Dune [Extended Edition]", NameScope.Vod);

        Assert.Equal("Dune [Extended Edition]", parsed.Title);
        Assert.Equal(new[] { "NL" }, parsed.Tags);
    }

    [Fact]
    public void UnicodePipePrefixIsExtracted()
    {
        NameNormalizationService service = CreateService();

        ParsedName parsed = service.Normalize("┃NL┃ Juliet", NameScope.Series);

        Assert.Equal("Juliet", parsed.Title);
        Assert.Equal(new[] { "NL" }, parsed.Tags);
    }

    [Fact]
    public void UppercaseBlockPrefixesAreExtractedConservatively()
    {
        NameNormalizationService service = CreateService();

        ParsedName parsed = service.Normalize("NL ▉ SPORTS ▉ HBO", NameScope.LiveChannel);

        Assert.Equal("HBO", parsed.Title);
        Assert.Equal(new[] { "NL", "SPORTS" }, parsed.Tags);
    }

    [Fact]
    public void CatastrophicRegexTimesOutAndIsDisabled()
    {
        NameNormalizationService service = CreateService();
        Assert.Empty(service.UpdateRules("^(a+)+$ => removed"));
        string input = new string('a', 10_000) + "!";
        Stopwatch stopwatch = Stopwatch.StartNew();

        ParsedName parsed = service.Normalize(input, NameScope.LiveProgram);
        stopwatch.Stop();

        Assert.Equal(input, parsed.Title);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Regex took {stopwatch.Elapsed}.");
        Assert.Equal(input, service.Normalize(input, NameScope.LiveProgram).Title);
    }

    private static NameNormalizationService CreateService()
    {
        return new NameNormalizationService(NullLogger<NameNormalizationService>.Instance);
    }
}
