using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Models.Api;

namespace SIT.DepartmentSystem.Web.Services;

public sealed class AssignableEngineerDirectory(
    AppDbContext db,
    SharedIdentityProvisioningClient provisioningClient)
{
    public async Task<IReadOnlyList<AssignableEngineerDto>> GetForRecordAsync(
        Guid recordId,
        CancellationToken cancellationToken = default)
    {
        var record = await db.ModuleRecords
            .AsNoTracking()
            .Where(x => x.Id == recordId)
            .Select(x => new { x.Location, x.Team })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException("找不到主單。");

        var team = Clean(record.Team);
        if (team is null || !DepartmentFamilyMatcher.IsRecognizedLocation(record.Location))
            return [];

        var accounts = await provisioningClient.ListAccountsAsync(cancellationToken);
        return accounts
            .Where(x =>
                DepartmentFamilyMatcher.MatchesLocation(x.Department, record.Location) &&
                string.Equals(Clean(x.TeamCode), team, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.AccountStatus, "Active", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.SystemAccessStatus, "Active", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.CsitRole, "CsitStaff", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(x.Account))
            .OrderBy(x => string.IsNullOrWhiteSpace(x.DisplayName) ? x.Account : x.DisplayName,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Account, StringComparer.OrdinalIgnoreCase)
            .Select(x => new AssignableEngineerDto(
                x.IdentityUserId,
                x.Account.Trim(),
                Clean(x.DisplayName) ?? string.Empty,
                Clean(x.EmployeeNo)))
            .ToList();
    }

    public async Task<string?> ResolveAssignableAccountAsync(
        Guid recordId,
        string? account,
        CancellationToken cancellationToken = default)
    {
        var candidate = Clean(account);
        if (candidate is null) return null;

        var engineers = await GetForRecordAsync(recordId, cancellationToken);
        var match = engineers.FirstOrDefault(x =>
            string.Equals(x.Account, candidate, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            throw new ArgumentException("指派工程師必須符合目前主單的地點、Department family 與 Team。");

        return match.Account;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
