using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace SIT.DepartmentSystem.Web.Services;

public sealed class BusinessWriteRequirement : IAuthorizationRequirement;

public sealed class BusinessWriteHandler : AuthorizationHandler<BusinessWriteRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        BusinessWriteRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated == true &&
            (context.User.IsInRole("Admin") || context.User.HasClaim(
                SystemAuthorization.DataAccessLevelClaim,
                SystemAuthorization.DataAccessLevels.Editor)))
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}

public interface IBusinessWriteAuthorizationGuard
{
    Task DemandAsync(CancellationToken cancellationToken = default);
}

public sealed class BusinessWriteAuthorizationGuard(
    IHttpContextAccessor httpContextAccessor,
    AuthenticationStateProvider authenticationStateProvider,
    IAuthorizationService authorization) : IBusinessWriteAuthorizationGuard
{
    public async Task DemandAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var principal = httpContextAccessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
            principal = (await authenticationStateProvider.GetAuthenticationStateAsync()).User;
        var result = await authorization.AuthorizeAsync(
            principal,
            SystemAuthorization.Policies.BusinessWrite);
        if (!result.Succeeded)
            throw new UnauthorizedAccessException("This account has read-only data access.");
    }
}
