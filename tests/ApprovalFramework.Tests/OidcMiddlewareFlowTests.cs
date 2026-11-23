using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Models;
using SIT.DepartmentSystem.Web.Services;

namespace ApprovalFramework.Tests;

public sealed class OidcMiddlewareFlowTests
{
    [Theory]
    [InlineData("Success")]
    [InlineData("MissingCorrelation")]
    [InlineData("InvalidState")]
    [InlineData("InvalidNonce")]
    [InlineData("TokenUnavailable")]
    [InlineData("InvalidRole")]
    [InlineData("BindingConflict")]
    [InlineData("SessionRejected")]
    [InlineData("RdApplicant")]
    public async Task RealMiddleware_HandlesCodeFlowAndFailureStages(string scenario)
    {
        using var provider = new FakeIdentityProvider();
        using var logs = new OidcLogCollector();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SharedIdentity:Authority"] = FakeIdentityProvider.Issuer,
            ["SharedIdentity:ClientId"] = "csit-test",
            ["SharedIdentity:ClientSecret"] = "isolated-test-only",
            ["DevDeployment:Sha"] = new string('a', 40),
            ["SharedIdentity:SessionDirectory"] = Path.Combine(Path.GetTempPath(),
                "csit-oidc-tests", Guid.NewGuid().ToString("N"))
        });
        builder.Logging.AddProvider(logs);
        builder.Services.AddDbContextFactory<AppDbContext>(options =>
            options.UseInMemoryDatabase("oidc-" + Guid.NewGuid().ToString("N")));
        builder.Services.AddScoped<SharedIdentityClaimsAdapter>();
        builder.Services.AddScoped<SharedIdentityCookieEvents>();
        builder.Services.AddSingleton<CsitOidcSessionStore>();
        builder.Services.AddHttpClient<SharedIdentitySessionValidator>()
            .ConfigurePrimaryHttpMessageHandler(() => provider);
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = "CSIT.Dev.Oidc";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.Always;
                options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
                options.EventsType = typeof(SharedIdentityCookieEvents);
                options.LoginPath = "/signin";
                options.AccessDeniedPath = "/access-denied";
            })
            .AddOpenIdConnect("oidc", options =>
            {
                SharedIdentityOidcConfiguration.Apply(options, FakeIdentityProvider.Issuer,
                    "csit-test", "isolated-test-only");
                options.BackchannelHttpHandler = provider;
            });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(SystemAuthorization.Policies.CsitStaff, policy =>
                policy.RequireAuthenticatedUser().RequireClaim(SystemAuthorization.AccessScopeClaim,
                    SystemAuthorization.AccessScopes.CsitStaff));
            options.AddPolicy(SystemAuthorization.Policies.RdApplicant, policy =>
                policy.RequireAuthenticatedUser().RequireClaim(SystemAuthorization.AccessScopeClaim,
                    SystemAuthorization.AccessScopes.RdApplicant));
        });

        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/signin", async context =>
            await context.ChallengeAsync("oidc", new AuthenticationProperties
            {
                RedirectUri = scenario == "RdApplicant" ? "/home" : "/protected"
            }));
        app.MapGet("/protected", () => "protected page reached").RequireAuthorization();
        app.MapGet("/rd", () => "rd page reached")
            .RequireAuthorization(SystemAuthorization.Policies.RdApplicant);
        app.MapGet("/home", () => "staff page reached")
            .RequireAuthorization(SystemAuthorization.Policies.CsitStaff);
        app.MapGet("/access-denied", () => Results.StatusCode(StatusCodes.Status403Forbidden));
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://csit.test");

        var dbFactory = app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.Users.Add(new AppUser
            {
                Account = "testreader", IdentityUserId = scenario == "BindingConflict"
                    ? Guid.NewGuid() : FakeIdentityProvider.Subject,
                DisplayName = "Before Login", Department = "DEV", Email = "before@example.test"
            });
            await db.SaveChangesAsync();
        }

        using var challenge = await client.GetAsync("/signin");
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        var authorize = challenge.Headers.Location!;
        var query = ParseQuery(authorize.Query);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("csit-test", query["client_id"]);
        Assert.Equal("https://csit.test/signin-oidc", query["redirect_uri"]);
        provider.Nonce = query["nonce"];
        provider.InvalidRole = scenario == "InvalidRole";
        provider.InvalidNonce = scenario == "InvalidNonce";
        provider.TokenUnavailable = scenario == "TokenUnavailable";
        provider.SessionRejected = scenario == "SessionRejected";
        provider.RdApplicant = scenario == "RdApplicant";

        var cookies = challenge.Headers.GetValues("Set-Cookie")
            .Select(header => header.Split(';')[0]).ToArray();
        Assert.Equal(2, cookies.Length);
        if (scenario == "MissingCorrelation")
            cookies = cookies.Where(cookie => !cookie.StartsWith(".AspNetCore.Correlation.",
                StringComparison.Ordinal)).ToArray();
        var state = scenario == "InvalidState" ? "invalid-protected-state" : query["state"];
        using var callback = new HttpRequestMessage(HttpMethod.Post, "/signin-oidc")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"] = "test-authorization-code", ["state"] = state
            })
        };
        callback.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies));
        using var result = await client.SendAsync(callback);

        if (scenario is "Success" or "SessionRejected" or "RdApplicant")
        {
            Assert.Equal(HttpStatusCode.Redirect, result.StatusCode);
            var landing = scenario == "RdApplicant" ? "/rd" : "/protected";
            Assert.Equal(landing, result.Headers.Location?.OriginalString);
            var appCookie = result.Headers.GetValues("Set-Cookie")
                .Single(value => value.StartsWith("CSIT.Dev.Oidc=", StringComparison.Ordinal))
                .Split(';')[0];
            using var protectedRequest = new HttpRequestMessage(HttpMethod.Get, landing);
            protectedRequest.Headers.TryAddWithoutValidation("Cookie", appCookie);
            using var protectedResponse = await client.SendAsync(protectedRequest);
            Assert.Equal(scenario == "SessionRejected" ? HttpStatusCode.Redirect : HttpStatusCode.OK,
                protectedResponse.StatusCode);
            if (scenario != "SessionRejected")
                Assert.Equal(scenario == "RdApplicant" ? "rd page reached" : "protected page reached",
                    await protectedResponse.Content.ReadAsStringAsync());
            if (scenario == "RdApplicant")
            {
                using var deniedRequest = new HttpRequestMessage(HttpMethod.Get, "/home");
                deniedRequest.Headers.TryAddWithoutValidation("Cookie", appCookie);
                using var denied = await client.SendAsync(deniedRequest);
                Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
                Assert.Equal("/access-denied", new Uri(client.BaseAddress!, denied.Headers.Location!).AbsolutePath);
                using var deniedPage = await client.GetAsync(denied.Headers.Location);
                Assert.Equal(HttpStatusCode.Forbidden, deniedPage.StatusCode);
            }
            Assert.Equal(scenario == "RdApplicant" ? 2 : 1, provider.SessionChecks);
            Assert.Contains(logs.Entries, entry => entry.Contains("Stage=CookieIssued"));
            Assert.Contains(logs.Entries, entry => entry.Contains("Stage=SessionValidation") &&
                entry.Contains(scenario == "SessionRejected" ? "Kind=IdentityStatus" : "Kind=Accepted"));
            var challengeFlow = Regex.Match(logs.Entries.Single(entry => entry.Contains("Stage=Challenge")),
                @"FlowId=([0-9a-f]{32})").Groups[1].Value;
            var exchangeFlow = Regex.Match(logs.Entries.Single(entry => entry.Contains("Stage=CodeExchangeStart")),
                @"FlowId=([0-9a-f]{32})").Groups[1].Value;
            Assert.Equal(32, challengeFlow.Length);
            Assert.Equal(challengeFlow, exchangeFlow);
        }
        else
        {
            Assert.NotEqual(HttpStatusCode.OK, result.StatusCode);
            Assert.DoesNotContain(result.Headers.TryGetValues("Set-Cookie", out var values)
                ? values : [], value => value.StartsWith("CSIT.Dev.Oidc=", StringComparison.Ordinal));
            if (scenario is "MissingCorrelation" or "InvalidState") Assert.Equal(0, provider.TokenCalls);
            if (scenario == "TokenUnavailable") Assert.Equal(1, provider.TokenCalls);
            if (scenario == "InvalidRole")
                Assert.Contains(logs.Entries, entry => entry.Contains("Kind=AccessClaims"));
            if (scenario == "BindingConflict")
                Assert.Contains(logs.Entries, entry => entry.Contains("Kind=BindingConflict"));
            if (scenario == "InvalidNonce")
            {
                Assert.DoesNotContain(logs.Entries, entry => entry.Contains("Stage=ClaimsMapping"));
                await using var db = await dbFactory.CreateDbContextAsync();
                Assert.Equal("Before Login", (await db.Users.SingleAsync()).DisplayName);
            }
            var expectedKind = scenario switch
            {
                "MissingCorrelation" => "Correlation",
                "InvalidState" => "State",
                "InvalidNonce" => "Nonce",
                "TokenUnavailable" => "TokenEndpoint",
                _ => "Unknown"
            };
            if (scenario is not ("InvalidRole" or "BindingConflict"))
                Assert.Contains(logs.Entries, entry =>
                    (entry.Contains("Stage=AuthenticationFailure") || entry.Contains("Stage=RemoteFailure")) &&
                    entry.Contains("Kind=" + expectedKind));
        }

        Assert.Contains(logs.Entries, entry => entry.Contains("Stage=Challenge"));
        Assert.Contains(logs.Entries, entry => entry.Contains("Stage=CallbackMessage"));
        Assert.DoesNotContain("test-authorization-code", string.Join('\n', logs.Entries));
        Assert.DoesNotContain(query["state"], string.Join('\n', logs.Entries));
        Assert.DoesNotContain(provider.LastToken, string.Join('\n', logs.Entries));
        foreach (var cookie in cookies)
            Assert.DoesNotContain(cookie, string.Join('\n', logs.Entries));
    }

    [Fact]
    public async Task NoCallback_CannotCreateAnAuthenticatedSession()
    {
        using var provider = new FakeIdentityProvider();
        using var logs = new OidcLogCollector();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(logs);
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options => options.LoginPath = "/signin")
            .AddOpenIdConnect("oidc", options =>
            {
                SharedIdentityOidcConfiguration.Apply(options, FakeIdentityProvider.Issuer,
                    "csit-test", "isolated-test-only");
                options.BackchannelHttpHandler = provider;
            });
        builder.Services.AddAuthorization();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/signin", async context => await context.ChallengeAsync("oidc"));
        app.MapGet("/protected", () => "secret").RequireAuthorization();
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://csit.test");
        using var challenge = await client.GetAsync("/signin");
        using var protectedResponse = await client.GetAsync("/protected");
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, protectedResponse.StatusCode);
        Assert.Equal(0, provider.TokenCalls);
        Assert.DoesNotContain(logs.Entries, entry => entry.Contains("Stage=CallbackMessage"));
    }

    private static Dictionary<string, string> ParseQuery(string query) => query.TrimStart('?')
        .Split('&').Select(part => part.Split('=', 2)).ToDictionary(
            part => WebUtility.UrlDecode(part[0]),
            part => WebUtility.UrlDecode(part.Length == 2 ? part[1] : ""));

    private sealed class FakeIdentityProvider : HttpMessageHandler
    {
        public const string Issuer = "https://idp.test/";
        public static readonly Guid Subject = Guid.Parse("11111111-2222-3333-4444-555555555555");
        private readonly RSA _rsa = RSA.Create(2048);
        private readonly RsaSecurityKey _key;
        public string Nonce { get; set; } = "";
        public bool InvalidRole { get; set; }
        public bool InvalidNonce { get; set; }
        public bool TokenUnavailable { get; set; }
        public bool SessionRejected { get; set; }
        public bool RdApplicant { get; set; }
        public int TokenCalls { get; private set; }
        public int SessionChecks { get; private set; }
        public string LastToken { get; private set; } = "token-not-created";

        public FakeIdentityProvider() => _key = new RsaSecurityKey(_rsa) { KeyId = "isolated-key" };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/.well-known/openid-configuration")
                return Json(new
                {
                    issuer = Issuer,
                    authorization_endpoint = Issuer + "connect/authorize",
                    token_endpoint = Issuer + "connect/token",
                    jwks_uri = Issuer + "jwks",
                    response_types_supported = new[] { "code" },
                    subject_types_supported = new[] { "public" },
                    id_token_signing_alg_values_supported = new[] { "RS256" }
                });
            if (path == "/jwks")
            {
                var p = _rsa.ExportParameters(false);
                return Json(new { keys = new[] { new
                {
                    kty = "RSA", use = "sig", kid = _key.KeyId, alg = "RS256",
                    n = Base64UrlEncoder.Encode(p.Modulus), e = Base64UrlEncoder.Encode(p.Exponent)
                } } });
            }
            if (path == "/connect/token")
            {
                TokenCalls++;
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                if (!body.Contains("code_verifier=", StringComparison.Ordinal) ||
                    !body.Contains("test-authorization-code", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.BadRequest);
                if (TokenUnavailable) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                var claims = new List<Claim>
                {
                    new("sub", Subject.ToString()), new("nonce", InvalidNonce ? "wrong-nonce" : Nonce),
                    new("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
                    new("security_version", "1"), new("authorization_version", "1"),
                    new("account", "testreader"), new("name", "Test Reader"),
                    new("department", "DEV"), new("email", "reader@example.test"),
                    new("data_access_level", "ReadOnly"),
                    new("csit_roles", InvalidRole ? "UnknownRole" : RdApplicant ? "RdApplicant" : "CsitStaff")
                };
                var token = new JwtSecurityToken(Issuer, "csit-test", claims,
                    DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5),
                    new SigningCredentials(_key, SecurityAlgorithms.RsaSha256));
                LastToken = new JwtSecurityTokenHandler().WriteToken(token);
                return Json(new { access_token = "isolated-access-token", id_token = LastToken,
                    token_type = "Bearer", expires_in = 300 });
            }
            if (path.StartsWith("/internal/csit/session-status/", StringComparison.Ordinal))
            {
                SessionChecks++;
                if (SessionRejected) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                return Json(new { account = "testreader", securityVersion = 1,
                    authorizationVersion = 1 });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };
    }

    private sealed class OidcLogCollector : ILoggerProvider
    {
        public List<string> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => new CollectorLogger(this,
            categoryName == "CSIT.Oidc");
        public void Dispose() { }

        private sealed class CollectorLogger(OidcLogCollector owner, bool collect) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => collect;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (collect) owner.Entries.Add(formatter(state, exception));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
