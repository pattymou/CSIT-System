using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
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
}
