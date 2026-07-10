using Jellyfin.Xtream.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Xtream.Tests;

public class ApiContractTests
{
    [Fact]
    public void ConfigurationApiUsesVersionedElevatedRoute()
    {
        Type controller = typeof(XtreamController);

        RouteAttribute route = Assert.Single(controller.GetCustomAttributes(typeof(RouteAttribute), inherit: true).Cast<RouteAttribute>());
        AuthorizeAttribute authorization = Assert.Single(controller.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Cast<AuthorizeAttribute>());

        Assert.Equal("Plugins/JellyfinXtream/v1", route.Template);
        Assert.Equal("RequiresElevation", authorization.Policy);
    }
}
