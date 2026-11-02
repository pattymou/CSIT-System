using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public sealed class DocumentLibraryDeletionAuthorizationService(
    IDbContextFactory<AppDbContext> dbFactory,
    IAuthorizationService authorization)
{
    public async Task<bool> CanDeleteAsync(ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        if (user.Identity?.IsAuthenticated != true ||
            !(await authorization.AuthorizeAsync(user,
                SystemAuthorization.Policies.BusinessWrite)).Succeeded ||
            !(await authorization.AuthorizeAsync(user,
                SystemAuthorization.Policies.Administration)).Succeeded)
            return false;

        if (user.IsInRole("Admin")) return true;
        var account = Approval.ApprovalIdentity.Account(user);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await (from routing in db.TeamRoutings.AsNoTracking()
            join team in db.SystemOptions.AsNoTracking() on routing.TeamOptionId equals team.Id
            where routing.IsEnabled && team.IsEnabled &&
                  team.Category == SystemOptionCategories.Team &&
                  routing.LeaderAccount.ToLower() == account
            select routing.Id).AnyAsync(cancellationToken);
    }
}
