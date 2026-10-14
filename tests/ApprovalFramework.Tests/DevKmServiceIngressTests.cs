using Microsoft.AspNetCore.Http;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace ApprovalFramework.Tests;

public sealed class DevKmServiceIngressTests
{
    private const string Document =
        "/api/integrations/km/v1/documents/11111111-1111-1111-1111-111111111111/versions/1/content";

    [Theory]
    [InlineData("GET", Document, true)]
    [InlineData("POST", "/api/integrations/km/v1/ingestion-status", true)]
    [InlineData("POST", Document, false)]
    [InlineData("GET", "/api/integrations/km/v1/ingestion-status", false)]
    [InlineData("GET", "/api/integrations/km/v1/documents/invalid/versions/1/content", false)]
    [InlineData("GET", "/api/integrations/km/v1/other", false)]
    [InlineData("GET", "/", false)]
    [InlineData("GET", "/signin", false)]
    [InlineData("GET", "/manual", false)]
    [InlineData("GET", "/app.css", false)]
    [InlineData("GET", "/api/other", false)]
    public void DedicatedPortOnlyAllowsIntegrationRoutes(string method, string path, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Connection.LocalPort = DevKmServiceIngress.Port;
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Host = new HostString("localhost", 7296);
        context.Request.Headers["X-Forwarded-Port"] = "7296";
        Assert.Equal(expected, DevKmServiceIngress.IsAllowed(context));
    }

    [Fact]
    public void LocalDevPortKeepsExistingRoutes()
    {
        var context = new DefaultHttpContext();
        context.Connection.LocalPort = 7296;
        context.Request.Path = "/";
        Assert.True(DevKmServiceIngress.IsAllowed(context));
    }
}
