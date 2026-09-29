using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Models;

namespace SIT.DepartmentSystem.Web.Services;

public sealed class SharedIdentityClaimsAdapter(IDbContextFactory<AppDbContext> dbFactory,
    ILogger<SharedIdentityClaimsAdapter> logger)
{
    public async Task<ClaimsPrincipal?> MapAsync(ClaimsPrincipal external, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(external.FindFirstValue("sub"), out var subject) ||
            !long.TryParse(external.FindFirstValue("security_version"), out var securityVersion) ||
            !long.TryParse(external.FindFirstValue("authorization_version"), out var authorizationVersion))
            return null;
        var account = external.FindFirstValue("account")?.Trim().ToLowerInvariant();
        var displayName = external.FindFirstValue("name")?.Trim();
        var department = external.FindFirstValue("department")?.Trim();
        var email = external.FindFirstValue("email")?.Trim();
        var dataAccessLevel = external.FindFirstValue(SystemAuthorization.DataAccessLevelClaim)?.Trim();
        var roles = (external.FindFirstValue("csit_roles") ?? string.Empty).Split(' ',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(displayName) ||
            string.IsNullOrWhiteSpace(department) || string.IsNullOrWhiteSpace(email) || email.Length > 200 ||
            dataAccessLevel is not (SystemAuthorization.DataAccessLevels.ReadOnly or
                SystemAuthorization.DataAccessLevels.Editor) ||
            roles.Contains("RdApplicant") == roles.Contains("CsitStaff") ||
            (roles.Contains("Admin") && !roles.Contains("CsitStaff")) ||
            roles.Except(["RdApplicant", "CsitStaff", "Admin"]).Any())
            return null;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var byId = await db.Users.SingleOrDefaultAsync(x => x.IdentityUserId == subject, cancellationToken);
        var accountMatches = await db.Users.Where(x => x.Account.ToLower() == account)
            .OrderBy(x => x.Id).Take(2).ToListAsync(cancellationToken);
        if (accountMatches.Count > 1)
        {
            logger.LogError("Shared Identity duplicate CSIT accounts for subject {Subject}", subject);
            return null;
        }
        var byAccount = accountMatches.SingleOrDefault();
        if ((byId is not null && !string.Equals(byId.Account, account, StringComparison.Ordinal)) ||
            (byId is not null && byAccount is not null && byId.Id != byAccount.Id) ||
            (byAccount?.IdentityUserId is Guid existingId && existingId != subject))
        {
            logger.LogError("Shared Identity account mapping conflict for subject {Subject}", subject);
            return null;
        }

        var user = byId ?? byAccount;
        if (user is null)
        {
            user = new AppUser { Account = account, IdentityUserId = subject };
            db.Users.Add(user);
        }
        else if (user.IdentityUserId is null)
            user.IdentityUserId = subject;

        user.DisplayName = displayName;
        user.Department = department;
        user.Email = email;
        // IsAdmin is legacy rollback data; OIDC authorization never reads or updates it.
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            logger.LogError(ex, "Shared Identity account mapping could not be saved for subject {Subject}", subject);
            return null;
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, account),
            new(ClaimTypes.Name, displayName),
            new(ClaimTypes.Role, roles.Contains("Admin") ? "Admin" : "User"),
            new("account", account),
            new("department", department),
            new("identity_sub", subject.ToString()),
            new("identity_security_version", securityVersion.ToString()),
            new("identity_authorization_version", authorizationVersion.ToString()),
            new(SystemAuthorization.DataAccessLevelClaim, dataAccessLevel),
            new(SystemAuthorization.AccessScopeClaim,
                roles.Contains("CsitStaff") ? SystemAuthorization.AccessScopes.CsitStaff
                                            : SystemAuthorization.AccessScopes.RdApplicant)
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }
}
