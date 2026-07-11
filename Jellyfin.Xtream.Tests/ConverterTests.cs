using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Client.Models;
using Newtonsoft.Json;

namespace Jellyfin.Xtream.Tests;

public class ConverterTests
{
    [Theory]
    [InlineData("\"1\"", true)]
    [InlineData("\"true\"", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("\"0\"", false)]
    public void StringBoolConverterAcceptsProviderVariants(string jsonValue, bool expected)
    {
        FlexibleBool value = JsonConvert.DeserializeObject<FlexibleBool>($"{{\"Value\":{jsonValue}}}")!;

        Assert.Equal(expected, value.Value);
    }

    [Fact]
    public void Base64ConverterFallsBackToPlainText()
    {
        EncodedText value = JsonConvert.DeserializeObject<EncodedText>("{\"value\":\"Not base64!\"}")!;

        Assert.Equal("Not base64!", value.Value);
    }

    [Theory]
    [InlineData("Test")]
    [InlineData("TWFu")]
    [InlineData("////")]
    public void Base64ConverterPreservesAmbiguousOrInvalidUtf8PlainText(string text)
    {
        string json = JsonConvert.SerializeObject(new { value = text });

        EncodedText value = JsonConvert.DeserializeObject<EncodedText>(json)!;

        Assert.Equal(text, value.Value);
    }

    [Fact]
    public void Base64ConverterDecodesExplicitPaddedUtf8()
    {
        EncodedText value = JsonConvert.DeserializeObject<EncodedText>("{\"value\":\"VGVzdA==\"}")!;

        Assert.Equal("Test", value.Value);
    }

    [Theory]
    [InlineData("1700000000", 2023, 11, 14)]
    [InlineData("1700000000000", 2023, 11, 14)]
    [InlineData("2024-04-05T12:30:00Z", 2024, 4, 5)]
    public void TolerantDateConverterAcceptsProviderVariants(
        string timestamp,
        int year,
        int month,
        int day)
    {
        string json = JsonConvert.SerializeObject(new { value = timestamp });

        FlexibleDate value = JsonConvert.DeserializeObject<FlexibleDate>(json)!;

        Assert.Equal(new DateTime(year, month, day), value.Value.Date);
        Assert.Equal(DateTimeKind.Utc, value.Value.Kind);
    }

    [Fact]
    public void TolerantDateConverterDefaultsMalformedValues()
    {
        FlexibleDates value = JsonConvert.DeserializeObject<FlexibleDates>(
            "{\"required\":\"not-a-date\",\"optional\":\"999999999999999999\"}")!;

        Assert.Equal(default, value.Required);
        Assert.Null(value.Optional);
    }

    [Fact]
    public void MissingOrMalformedEpisodeCollectionsBecomeEmpty()
    {
        SeriesStreamInfo value = JsonConvert.DeserializeObject<SeriesStreamInfo>(
            "{\"seasons\":null,\"info\":null,\"episodes\":\"unavailable\"}")!;

        Assert.Empty(value.Seasons);
        Assert.NotNull(value.Info);
        Assert.Empty(value.Episodes);
    }

    private sealed class FlexibleBool
    {
        [JsonConverter(typeof(StringBoolConverter))]
        public bool Value { get; set; }
    }

    private sealed class EncodedText
    {
        [JsonConverter(typeof(Base64Converter))]
        [JsonProperty("value")]
        public string Value { get; set; } = string.Empty;
    }

    private sealed class FlexibleDate
    {
        [JsonConverter(typeof(TolerantDateTimeConverter))]
        public DateTime Value { get; set; }
    }

    private sealed class FlexibleDates
    {
        [JsonConverter(typeof(TolerantDateTimeConverter))]
        public DateTime Required { get; set; }

        [JsonConverter(typeof(TolerantDateTimeConverter))]
        public DateTime? Optional { get; set; }
    }
}
