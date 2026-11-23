using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SIT.DepartmentSystem.Web.Controllers;
using SIT.DepartmentSystem.Web.Services;

namespace ApprovalFramework.Tests;

public sealed class AuthControllerRedirectTests
{
    [Theory]
    [InlineData(SystemAuthorization.AccessScopes.RdApplicant, "/rd")]
    [InlineData(SystemAuthorization.AccessScopes.CsitStaff, "/home")]
    public void AuthenticatedSigninAndRoot_DoNotStartAnotherOidcChallenge(string scope, string landing)
    {
        var controller = CreateController(scope);

        Assert.Equal(landing, Assert.IsType<RedirectResult>(controller.Root()).Url);
        Assert.Equal(landing, Assert.IsType<RedirectResult>(controller.LoginPage()).Url);
    }

    [Theory]
    [InlineData("/home", "/rd")]
    [InlineData("/home/", "/rd")]
    [InlineData("/home?source=login", "/rd")]
    [InlineData("/rd", "/rd")]
    [InlineData("/reservations", "/reservations")]
    public void RdLanding_AvoidsStaffOnlyHome(string requested, string expected)
    {
        Assert.Equal(expected, SharedIdentityOidcEvents.ResolveLanding(Principal(
            SystemAuthorization.AccessScopes.RdApplicant), requested));
    }

    [Fact]
    public void UnauthorizedOidcAccess_HasExplicitForbiddenResult()
    {
        var controller = CreateController(SystemAuthorization.AccessScopes.RdApplicant);
        var result = Assert.IsType<ObjectResult>(controller.AccessDenied());
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }

    [Fact]
    public async Task Logout_RevokesCookieThenOpensLoginPageWithoutOidcChallenge()
    {
        var controller = CreateController(SystemAuthorization.AccessScopes.RdApplicant);
        var authentication = new RecordingAuthenticationService();
        controller.HttpContext.RequestServices = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(authentication).BuildServiceProvider();

        Assert.Equal("/login?signedOut=1", Assert.IsType<RedirectResult>(await controller.Logout()).Url);
        Assert.Equal(CookieAuthenticationDefaults.AuthenticationScheme, authentication.SignedOutScheme);
        Assert.Equal("/login?signedOut=1", Assert.IsType<RedirectResult>(controller.SignedOut()).Url);
        Assert.False(authentication.Challenged);
    }

    private static AuthController CreateController(string scope)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["AuthenticationMode"] = "Oidc" }).Build();
        return new AuthController(null!, configuration)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = Principal(scope) }
            }
        };
    }

    private static ClaimsPrincipal Principal(string scope) => new(new ClaimsIdentity(
        [new Claim(SystemAuthorization.AccessScopeClaim, scope)], "test"));

    private sealed class RecordingAuthenticationService : IAuthenticationService
    {
        public string? SignedOutScheme { get; private set; }
        public bool Challenged { get; private set; }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.NoResult());
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            Challenged = true;
            return Task.CompletedTask;
        }
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal,
            AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            SignedOutScheme = scheme;
            return Task.CompletedTask;
        }
    }
}
