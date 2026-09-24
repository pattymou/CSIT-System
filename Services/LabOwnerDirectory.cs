using SIT.DepartmentSystem.Web.Models.Api;

namespace SIT.DepartmentSystem.Web.Services;

public sealed class LabOwnerDirectory(SharedIdentityProvisioningClient provisioningClient)
{
    public async Task<IReadOnlyList<LabOwnerOptionDto>> GetForLocationAsync(
        string? location,
        CancellationToken cancellationToken = default)
    {
        if (!DepartmentFamilyMatcher.IsRecognizedLocation(location))
            return [];

        var accounts = await provisioningClient.ListAccountsAsync(cancellationToken);
        return accounts
            .Where(x =>
                DepartmentFamilyMatcher.MatchesLocation(x.Department, location) &&
                string.Equals(x.AccountStatus, "Active", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.SystemAccessStatus, "Active", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.CsitRole, "CsitStaff", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(x.Account))
            .OrderBy(x => string.IsNullOrWhiteSpace(x.DisplayName) ? x.Account : x.DisplayName,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Account, StringComparer.OrdinalIgnoreCase)
            .Select(x => new LabOwnerOptionDto(
                x.IdentityUserId,
                x.Account.Trim(),
                Clean(x.DisplayName) ?? string.Empty,
                Clean(x.EmployeeNo)))
            .ToList();
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
