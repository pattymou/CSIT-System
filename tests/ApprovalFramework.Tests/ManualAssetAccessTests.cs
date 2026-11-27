using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Controllers;
using SIT.DepartmentSystem.Web.Services;

namespace ApprovalFramework.Tests;

public sealed class ManualAssetAccessTests
{
    [Fact]
    public async Task RdCannotReadInternalImageEvenByDirectUrl()
    {
        var root = Path.Combine(Path.GetTempPath(), "csit-manual-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "ManualAssets", "internal"));
        Directory.CreateDirectory(Path.Combine(root, "ManualAssets", "rd"));
        Directory.CreateDirectory(Path.Combine(root, "ManualAssets", "shared"));
        await File.WriteAllBytesAsync(Path.Combine(root, "ManualAssets", "internal", "staff.png"), [137, 80, 78, 71]);
        await File.WriteAllBytesAsync(Path.Combine(root, "ManualAssets", "rd", "home.png"), [137, 80, 78, 71]);
        await File.WriteAllBytesAsync(Path.Combine(root, "ManualAssets", "shared", "signed-out.png"), [137, 80, 78, 71]);

        try
        {
            const string scheme = "ManualTest";
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = root });
            builder.WebHost.UseTestServer();
            builder.Services.AddControllers().AddApplicationPart(typeof(ManualAssetsController).Assembly);
            builder.Services.AddAuthentication(scheme)
                .AddScheme<AuthenticationSchemeOptions, ManualTestAuthenticationHandler>(scheme, _ => { });
            builder.Services.AddAuthorization(options =>
            {
                options.AddPolicy(SystemAuthorization.Policies.CsitStaff, policy =>
                    policy.RequireAuthenticatedUser().RequireClaim(
                        SystemAuthorization.AccessScopeClaim, SystemAuthorization.AccessScopes.CsitStaff));
                options.AddPolicy(SystemAuthorization.Policies.RdApplicant, policy =>
                    policy.RequireAuthenticatedUser().RequireClaim(
                        SystemAuthorization.AccessScopeClaim, SystemAuthorization.AccessScopes.RdApplicant));
            });

            await using var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapControllers();
            await app.StartAsync();

            using var client = app.GetTestClient();
            using var rdRequest = new HttpRequestMessage(HttpMethod.Get, "/api/manual/assets/internal/staff.png");
            rdRequest.Headers.Add("X-Test-Access-Scope", SystemAuthorization.AccessScopes.RdApplicant);
            using var rdResponse = await client.SendAsync(rdRequest);
            Assert.Equal(HttpStatusCode.Forbidden, rdResponse.StatusCode);

            using var sharedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/manual/assets/shared/signed-out.png");
            sharedRequest.Headers.Add("X-Test-Access-Scope", SystemAuthorization.AccessScopes.RdApplicant);
            using var sharedResponse = await client.SendAsync(sharedRequest);
            Assert.Equal(HttpStatusCode.OK, sharedResponse.StatusCode);
            Assert.True(sharedResponse.Headers.CacheControl?.Private);
            Assert.True(sharedResponse.Headers.CacheControl?.NoStore);

            using var rdImageRequest = new HttpRequestMessage(HttpMethod.Get, "/api/manual/assets/rd/home.png");
            rdImageRequest.Headers.Add("X-Test-Access-Scope", SystemAuthorization.AccessScopes.RdApplicant);
            using var rdImageResponse = await client.SendAsync(rdImageRequest);
            Assert.Equal(HttpStatusCode.OK, rdImageResponse.StatusCode);

            using var anonymousRdResponse = await client.GetAsync("/api/manual/assets/rd/home.png");
            Assert.Equal(HttpStatusCode.Unauthorized, anonymousRdResponse.StatusCode);

            using var staffRequest = new HttpRequestMessage(HttpMethod.Get, "/api/manual/assets/internal/staff.png");
            staffRequest.Headers.Add("X-Test-Access-Scope", SystemAuthorization.AccessScopes.CsitStaff);
            using var staffResponse = await client.SendAsync(staffRequest);
            Assert.Equal(HttpStatusCode.OK, staffResponse.StatusCode);

            using var anonymousResponse = await client.GetAsync("/api/manual/assets/internal/staff.png");
            Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

            using var staticResponse = await client.GetAsync("/ManualAssets/internal/staff.png");
            Assert.Equal(HttpStatusCode.NotFound, staticResponse.StatusCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

internal sealed class ManualTestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var scope = Request.Headers["X-Test-Access-Scope"].ToString();
        if (scope.Length == 0)
            return Task.FromResult(AuthenticateResult.NoResult());

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(SystemAuthorization.AccessScopeClaim, scope)], Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(principal, Scheme.Name)));
    }
}
