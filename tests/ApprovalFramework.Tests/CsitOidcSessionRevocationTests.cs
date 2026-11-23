using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SIT.DepartmentSystem.Web.Services;

namespace ApprovalFramework.Tests;

public sealed class CsitOidcSessionRevocationTests
{
    [Fact]
    public async Task LogoutRevokesOnlyCurrentCookie_AndReplayFailsAfterRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "csit-session-replay", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var identity = new FakeIdentity();
        try
        {
            string firstCookie;
            string otherCookie;
            await using (var app = await StartAsync(root, identity))
            {
                using var client = Client(app);
                firstCookie = await LoginAsync(client);
                otherCookie = await LoginAsync(client);
                Assert.NotEqual(firstCookie, otherCookie);
                Assert.Equal(HttpStatusCode.OK, await GetStatusAsync(client, "/api/protected", firstCookie));
                Assert.Equal(HttpStatusCode.OK, await GetStatusAsync(client, "/api/protected", otherCookie));
                Assert.Equal(HttpStatusCode.OK, await GetStatusAsync(client, "/auth/logout", firstCookie));
                Assert.Equal(HttpStatusCode.Redirect, await GetStatusAsync(client, "/api/protected", firstCookie));
                Assert.Equal(HttpStatusCode.OK, await GetStatusAsync(client, "/api/protected", otherCookie));
            }

            // The cookie protection keys and session records persist across a new runtime instance.
            await using (var restarted = await StartAsync(root, identity))
            {
                using var client = Client(restarted);
                Assert.Equal(HttpStatusCode.Redirect, await GetStatusAsync(client, "/api/protected", firstCookie));
                Assert.Equal(HttpStatusCode.OK, await GetStatusAsync(client, "/api/protected", otherCookie));
                identity.AuthorizationVersion = 2;
                Assert.Equal(HttpStatusCode.Redirect, await GetStatusAsync(client, "/api/protected", otherCookie));
                identity.AuthorizationVersion = 1;
                identity.SecurityVersion = 2;
                Assert.Equal(HttpStatusCode.Redirect, await GetStatusAsync(client, "/api/protected", otherCookie));
                identity.SecurityVersion = 1;
                var freshCookie = await LoginAsync(client);
                Assert.Equal(HttpStatusCode.OK, await GetStatusAsync(client, "/api/protected", freshCookie));
                identity.Offline = true;
                Assert.Equal(HttpStatusCode.Redirect, await GetStatusAsync(client, "/api/protected", freshCookie));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ExpiredRecordsAreRemovedOnNextSignIn()
    {
        var root = Path.Combine(Path.GetTempPath(), "csit-session-expiry", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new CsitOidcSessionStore(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["SharedIdentity:SessionDirectory"] = root }).Build());
            var principal = Principal();
            Assert.False(store.IsActive(principal)); // Cookies from before this change fail closed.
            var id = store.Create(principal, DateTimeOffset.UtcNow.AddMilliseconds(50));
            principal.Identities.First().AddClaim(new Claim(CsitOidcSessionStore.ClaimType, id));
            await Task.Delay(100);
            Assert.False(store.IsActive(principal));
            store.Create(Principal(), DateTimeOffset.UtcNow.AddHours(1));
            Assert.Single(Directory.GetFiles(root, "*.session"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task<WebApplication> StartAsync(string root, FakeIdentity identity)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SharedIdentity:Authority"] = "https://identity.test",
            ["SharedIdentity:ClientId"] = "csit-test",
            ["SharedIdentity:ClientSecret"] = "isolated-test-only",
            ["SharedIdentity:SessionDirectory"] = Path.Combine(root, "sessions")
        });
        builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root, "keys")));
        builder.Services.AddSingleton<CsitOidcSessionStore>();
        builder.Services.AddScoped<SharedIdentityCookieEvents>();
        builder.Services.AddHttpClient<SharedIdentitySessionValidator>()
            .ConfigurePrimaryHttpMessageHandler(() => identity);
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = "CSIT.Dev.Oidc";
                options.EventsType = typeof(SharedIdentityCookieEvents);
                options.LoginPath = "/signin";
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = false;
            });
        builder.Services.AddAuthorization();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/login", async (HttpContext context) =>
        {
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, Principal());
            return Results.Ok();
        });
        app.MapGet("/auth/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok();
        }).RequireAuthorization();
        app.MapGet("/api/protected", () => Results.Ok()).RequireAuthorization();
        await app.StartAsync();
        return app;
    }

    private static ClaimsPrincipal Principal() => new(new ClaimsIdentity(new[]
    {
        new Claim("identity_sub", "11111111-2222-3333-4444-555555555555"),
        new Claim("identity_security_version", "1"),
        new Claim("identity_authorization_version", "1"),
        new Claim("account", "testreader")
    }, CookieAuthenticationDefaults.AuthenticationScheme));

    private static HttpClient Client(WebApplication app)
    {
        var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://csit.test");
        return client;
    }

    private static async Task<string> LoginAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/login");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith("CSIT.Dev.Oidc=", StringComparison.Ordinal)).Split(';')[0];
    }

    private static async Task<HttpStatusCode> GetStatusAsync(HttpClient client, string path, string cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private sealed class FakeIdentity : HttpMessageHandler
    {
        public long AuthorizationVersion { get; set; } = 1;
        public long SecurityVersion { get; set; } = 1;
        public bool Offline { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(Offline
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"account\":\"testreader\",\"securityVersion\":{SecurityVersion}," +
                                            $"\"authorizationVersion\":{AuthorizationVersion}}}")
            });
    }
}
