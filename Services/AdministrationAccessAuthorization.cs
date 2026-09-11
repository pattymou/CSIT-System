using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;

namespace SIT.DepartmentSystem.Web.Services;

public sealed class AdministrationAccessRequirement : IAuthorizationRequirement;

public sealed class AdministrationAccessHandler(IDbContextFactory<AppDbContext> dbFactory)
    : AuthorizationHandler<AdministrationAccessRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AdministrationAccessRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true) return;

        if (context.User.IsInRole("Admin"))
        {
            context.Succeed(requirement);
            return;
        }

        if (!context.User.HasClaim(
                SystemAuthorization.AccessScopeClaim,
                SystemAuthorization.AccessScopes.CsitStaff))
            return;

        var account = context.User.FindFirstValue("account")
            ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(account)) return;

        var normalizedAccount = account.Trim().ToLowerInvariant();
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.TeamRoutings.AsNoTracking().AnyAsync(x =>
                x.IsEnabled && x.LeaderAccount.ToLower() == normalizedAccount))
            context.Succeed(requirement);
    }
}
