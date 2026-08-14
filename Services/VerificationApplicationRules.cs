using System.Security.Claims;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Services;

internal static class VerificationApplicationSecurity
{
    public static string GetAccount(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.Identity?.IsAuthenticated != true) throw new UnauthorizedAccessException("Authentication is required.");
        var account = user.FindFirstValue("account") ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(account)) throw new UnauthorizedAccessException("Authenticated account claim is missing.");
        return account.Trim().ToLowerInvariant();
    }

    public static void EnsureApplicantOwnership(VerificationApplication entity, string account)
    {
        if (!string.Equals(entity.ApplicantAccount, account, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The application does not belong to the authenticated applicant.");
    }

    public static void EnsureLeaderOwnership(VerificationApplication entity, string account)
    {
        if (!string.Equals(entity.AssignedLeaderAccount, account, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The application is assigned to another team leader.");
    }
}

internal static class VerificationApplicationRoutingRules
{
    public static VerificationApplicationRouting Resolve(VerificationCategory category, ModuleEntity? module)
    {
        ArgumentNullException.ThrowIfNull(category);
        if (!category.IsActive) throw new InvalidOperationException("Verification category is inactive.");
        if (string.IsNullOrWhiteSpace(category.LeaderAccount)) throw new InvalidOperationException("Verification category has no leader account configured.");
        if (module is null || !module.IsEnabled || !string.Equals(module.Code, category.ModuleCode, StringComparison.Ordinal))
            throw new InvalidOperationException("Verification category does not reference an enabled module.");

        return new VerificationApplicationRouting(
            category.Id,
            Required(category.Code, "category code"),
            Required(category.Name, "category name"),
            module.Code,
            category.LeaderAccount.Trim().ToLowerInvariant(),
            string.IsNullOrWhiteSpace(category.LeaderDisplayName) ? null : category.LeaderDisplayName.Trim());
    }

    private static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"Verification category has no {name} configured.") : value.Trim();
}
