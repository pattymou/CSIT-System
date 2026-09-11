using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;

namespace SIT.DepartmentSystem.Web.Services;

internal sealed record ApparatusCustodianProfile(string DisplayName, string? Department);

internal static class ApparatusCustodianResolver
{
    public static async Task<IReadOnlyDictionary<string, ApparatusCustodianProfile>> LoadDisplayNamesAsync(
        AppDbContext db,
        IEnumerable<string?> accounts,
        CancellationToken cancellationToken = default)
    {
        var normalizedAccounts = accounts
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (normalizedAccounts.Length == 0)
            return new Dictionary<string, ApparatusCustodianProfile>(StringComparer.OrdinalIgnoreCase);

        var users = await db.Users.AsNoTracking()
            .Where(x => normalizedAccounts.Contains(x.Account.ToLower()))
            .Select(x => new { x.Account, x.DisplayName, x.Department })
            .ToListAsync(cancellationToken);

        return users.ToDictionary(
            x => x.Account.Trim(),
            x => new ApparatusCustodianProfile(x.DisplayName, x.Department),
            StringComparer.OrdinalIgnoreCase);
    }

    public static string? GetDisplayName(
        IReadOnlyDictionary<string, ApparatusCustodianProfile> displayNames,
        string? account)
    {
        if (string.IsNullOrWhiteSpace(account)) return null;
        return displayNames.TryGetValue(account.Trim(), out var profile)
            ? profile.DisplayName
            : null;
    }

    public static string? GetDepartment(
        IReadOnlyDictionary<string, ApparatusCustodianProfile> displayNames,
        string? account)
    {
        if (string.IsNullOrWhiteSpace(account)) return null;
        return displayNames.TryGetValue(account.Trim(), out var profile)
            ? profile.Department
            : null;
    }
}
