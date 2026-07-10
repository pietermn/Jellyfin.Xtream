using Jellyfin.Xtream.Client;
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
}
