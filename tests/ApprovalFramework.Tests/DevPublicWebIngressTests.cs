using Microsoft.AspNetCore.Http;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace ApprovalFramework.Tests;

public sealed class DevPublicWebIngressTests
{
    [Theory]
    [InlineData("/", true)]
    [InlineData("/signin", true)]
    [InlineData("/signin-oidc", true)]
    [InlineData("/_blazor", true)]
    [InlineData("/app.css", true)]
    [InlineData("/swagger", false)]
    [InlineData("/swagger/v1/swagger.json", false)]
    [InlineData("/_framework/debug", false)]
    [InlineData("/_vs/browserLink", false)]
    [InlineData("/counter", false)]
    [InlineData("/login", false)]
    public void PublicPortAllowsWebsiteButBlocksSwagger(string path, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Connection.LocalPort = DevPublicWebIngress.Port;
        context.Request.Host = new HostString(DevKmServiceIngress.Hostname, DevPublicWebIngress.Port);
        context.Request.Path = path;
        Assert.Equal(expected, DevPublicWebIngress.IsAllowed(context));
    }

    [Theory]
    [InlineData("localhost", 7445)]
    [InlineData("csit-dev-da40.duckdns.org", 7296)]
    [InlineData("other.example", 7445)]
    public void PublicPortRejectsUnexpectedHosts(string host, int port)
    {
        var context = new DefaultHttpContext();
        context.Connection.LocalPort = DevPublicWebIngress.Port;
        context.Request.Host = new HostString(host, port);
        Assert.False(DevPublicWebIngress.IsAllowed(context));
    }

    [Fact]
    public void OtherPortsKeepExistingBehavior()
    {
        var context = new DefaultHttpContext();
        context.Connection.LocalPort = 7296;
        context.Request.Host = new HostString("localhost", 7296);
        context.Request.Path = "/signin";
        Assert.True(DevPublicWebIngress.IsAllowed(context));
    }
}
