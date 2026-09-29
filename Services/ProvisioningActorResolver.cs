using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models;

namespace SIT.DepartmentSystem.Web.Services;

public sealed class ProvisioningActorResolver(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<ProvisioningActorContext> ResolveAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        if (user.Identity?.IsAuthenticated != true ||
            !Guid.TryParse(user.FindFirstValue("identity_sub"), out var identityUserId))
            throw new UnauthorizedAccessException("A Shared Identity session is required.");

        var account = user.FindFirstValue("account")?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(account))
            throw new UnauthorizedAccessException("The authenticated account is unavailable.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        IQueryable<SystemOption> teams = db.SystemOptions.AsNoTracking().Where(x =>
            x.Category == SystemOptionCategories.Team && x.IsEnabled);

        var isAdmin = user.IsInRole("Admin");
        if (!isAdmin)
        {
            if (!user.HasClaim(SystemAuthorization.AccessScopeClaim,
                    SystemAuthorization.AccessScopes.CsitStaff))
                throw new UnauthorizedAccessException("Only an administrator or active Team Leader may create accounts.");

            teams = from team in teams
                    join routing in db.TeamRoutings.AsNoTracking()
                        on team.Id equals routing.TeamOptionId
                    where routing.IsEnabled && routing.LeaderAccount.ToLower() == account
                    select team;
        }

        var available = await teams
            .OrderBy(x => x.Sort)
            .ThenBy(x => x.Name)
            .Select(x => new ProvisionableTeam(x.Value, x.Name))
            .ToListAsync(cancellationToken);
        available = available
            .Where(x => !string.IsNullOrWhiteSpace(x.Code))
            .DistinctBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (available.Count == 0)
            throw new UnauthorizedAccessException("No active Team is available for this account.");

        return new ProvisioningActorContext(identityUserId, account, isAdmin, available);
    }

    public static ProvisionableTeam AuthorizeTarget(
        ProvisioningActorContext actor,
        string teamCode,
        CsitProvisioningRole role)
    {
        if (!actor.IsAdmin && role == CsitProvisioningRole.Admin)
            throw new UnauthorizedAccessException("A Team Leader cannot create an Admin account.");
        return actor.Teams.SingleOrDefault(x =>
                   string.Equals(x.Code, teamCode?.Trim(), StringComparison.OrdinalIgnoreCase))
               ?? throw new UnauthorizedAccessException("The selected Team is outside the actor's active scope.");
    }
}
