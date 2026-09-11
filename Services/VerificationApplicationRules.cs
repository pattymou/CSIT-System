using System.Security.Claims;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Services;

internal static class VerificationApplicationWorkflow
{
    // Verification Application creates records in the existing verification module.
    public const string ModuleCode = "verification";
}

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
