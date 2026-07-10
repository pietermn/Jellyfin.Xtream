namespace Jellyfin.Xtream.Tests;

public class SmokeTests
{
    [Fact]
    public void TestAssemblyLoads()
    {
        Assert.NotNull(typeof(Plugin).Assembly);
    }
}
