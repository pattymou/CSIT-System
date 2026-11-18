using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SIT.DepartmentSystem.Web.Controllers;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace ApprovalFramework.Tests;

public sealed class DevKmServiceIngressTests
{
    private const string Document =
        "/api/integrations/km/v1/documents/11111111-1111-1111-1111-111111111111/versions/1/content";
    private const string Vendor =
        "/api/integrations/km/v1/vendor-files/11111111-1111-1111-1111-111111111111/content";
    private const string Equipment =
        "/api/integrations/km/v1/equipment-documents/11111111-1111-1111-1111-111111111111/content";

    [Theory]
    [InlineData("GET", Document, true)]
    [InlineData("GET", Vendor, true)]
    [InlineData("GET", Equipment, true)]
    [InlineData("POST", "/api/integrations/km/v1/ingestion-status", true)]
    [InlineData("POST", "/api/integrations/km/v2/document-jobs/status", true)]
    [InlineData("POST", "/api/integrations/km/v2/analysis-results", true)]
    [InlineData("POST", "/api/integrations/km/v2/equipment-aggregate-analyses/status", true)]
    [InlineData("POST", "/api/integrations/km/v2/document-deletions/status", true)]
    [InlineData("POST", "/api/integrations/km/v2/document-metadata-changes/status", true)]
    [InlineData("POST", Document, false)]
    [InlineData("GET", "/api/integrations/km/v1/ingestion-status", false)]
    [InlineData("GET", "/api/integrations/km/v2/document-jobs/status", false)]
    [InlineData("GET", "/api/integrations/km/v2/analysis-results", false)]
    [InlineData("GET", "/api/integrations/km/v2/equipment-aggregate-analyses/status", false)]
    [InlineData("GET", "/api/integrations/km/v2/document-deletions/status", false)]
    [InlineData("POST", "/api/integrations/km/drafts/equipment-document-deletion-status", false)]
    [InlineData("GET", "/api/integrations/km/v2/document-metadata-changes/status", false)]
    [InlineData("GET", "/api/integrations/km/v1/documents/invalid/versions/1/content", false)]
    [InlineData("GET", "/api/integrations/km/v1/vendor-files/invalid/content", false)]
    [InlineData("GET", "/api/integrations/km/v1/equipment-documents/invalid/content", false)]
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

    [Fact]
    public async Task EquipmentAggregateCallbackOn7444WithoutTokenReachesAuthenticationChallenge()
    {
        const string scheme = "IsolatedMockKmCallback";
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddControllers().AddApplicationPart(typeof(KmAnalysisCallbackController).Assembly);
        builder.Services.AddAuthentication(scheme)
            .AddScheme<AuthenticationSchemeOptions, MockKmCallbackAuthenticationHandler>(scheme, _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy(
            KmIntegrationAuthorization.AnalysisStatusWritePolicy,
            policy => policy.AddAuthenticationSchemes(scheme).RequireAuthenticatedUser()));

        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Connection.LocalPort = DevKmServiceIngress.Port;
            if (!DevKmServiceIngress.IsAllowed(context))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next();
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();

        using var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync(
            "/api/integrations/km/v2/equipment-aggregate-analyses/status", new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
