using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;

namespace SIT.DepartmentSystem.Web.Services;

public static class DevFormalStorageGuard
{
    public const string Root = @"\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV";
    public const string BusinessHierarchyKey = "FormalStorage:BusinessHierarchyEnabled";
    public const string ApprovedWorkspaceRoot = @"\\AI-NAS\Workspace_B";
    public static readonly IReadOnlyDictionary<string, string> ConfigurationRoots =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DocumentStorage:RootPath"] = Path.Combine(Root, "DocumentLibrary"),
            ["RawData:NasRootPath"] = Path.Combine(Root, "RawData")
        };

    public static readonly IReadOnlyDictionary<(string Category, string Name), string> LegacyDatabaseRoots =
        new Dictionary<(string Category, string Name), string>
        {
            [("AssetRootPath", "equipment")] = Path.Combine(Root, "Assets", "equipment"),
            [("AssetRootPath", "goods")] = Path.Combine(Root, "Assets", "goods"),
            [("ThreeLevelRootPath", "certification")] = Path.Combine(Root, "ThreeLevel", "certification"),
            [("ThreeLevelRootPath", "verification")] = Path.Combine(Root, "ThreeLevel", "verification"),
            [("TestReportRootPath", "default")] = Path.Combine(Root, "TestReports")
        };

    public static readonly IReadOnlyDictionary<(string Category, string Name), string> DatabaseRoots =
        new Dictionary<(string Category, string Name), string>
        {
            [("AssetRootPath", "equipment")] = Root,
            [("AssetRootPath", "goods")] = Root,
            [("ThreeLevelRootPath", "certification")] = Root,
            [("ThreeLevelRootPath", "verification")] = Root,
            [("TestReportRootPath", "default")] = Root
        };

    public static void ValidateConfiguration(IConfiguration configuration, bool requireAccessible)
    {
        var businessHierarchyEnabled = configuration.GetValue<bool>(BusinessHierarchyKey);
        if (businessHierarchyEnabled)
        {
            RequireExactRoot("DocumentStorage:RootPath", configuration["DocumentStorage:RootPath"],
                Root, requireAccessible);
            RequireSeparateTechnicalRoot(configuration["RawData:NasRootPath"], requireAccessible);
        }
        else
        {
            foreach (var expected in ConfigurationRoots)
                RequireExactRoot(expected.Key, configuration[expected.Key], expected.Value, requireAccessible);
        }

        var staging = configuration["DocumentStaging:RootPath"]?.Trim();
        if (string.IsNullOrWhiteSpace(staging) || !Path.IsPathFullyQualified(staging))
            throw new InvalidOperationException("DocumentStaging:RootPath must be fully qualified.");
        if (IsAtOrBelow(staging, Root) || (businessHierarchyEnabled &&
            IsAtOrBelow(staging, configuration["RawData:NasRootPath"]!)))
            throw new InvalidOperationException("DEV staging must remain separate from formal NAS storage.");
    }

    public static async Task ValidateDatabaseAsync(AppDbContext db, bool businessHierarchyEnabled,
        bool requireAccessible,
        CancellationToken cancellationToken = default)
    {
        var expectedRoots = businessHierarchyEnabled ? DatabaseRoots : LegacyDatabaseRoots;
        var categories = expectedRoots.Keys.Select(x => x.Category).Distinct().ToArray();
        var rows = await db.SystemOptions.AsNoTracking()
            .Where(x => x.IsEnabled && categories.Contains(x.Category))
            .Select(x => new { x.Category, x.Name, x.Value })
            .ToListAsync(cancellationToken);

        foreach (var expected in expectedRoots)
        {
            var matches = rows.Where(x =>
                string.Equals(x.Category, expected.Key.Category, StringComparison.Ordinal) &&
                string.Equals(x.Name, expected.Key.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1)
                throw new InvalidOperationException(
                    $"DEV formal storage requires exactly one enabled {expected.Key.Category}/{expected.Key.Name} root.");
            RequireExactRoot($"{expected.Key.Category}/{expected.Key.Name}", matches[0].Value,
                expected.Value, requireAccessible);
        }
    }

    public static bool IsAtOrBelow(string candidate, string root)
    {
        var candidatePath = Normalize(candidate);
        var rootPath = Normalize(root);
        return candidatePath.Equals(rootPath, StringComparison.OrdinalIgnoreCase) ||
               candidatePath.StartsWith(rootPath + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static void RequireExactRoot(string key, string? actual, string expected,
        bool requireAccessible)
    {
        if (string.IsNullOrWhiteSpace(actual) ||
            !Normalize(actual).Equals(Normalize(expected), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"DEV formal storage root {key} must be {expected}.");
        if (requireAccessible && !Directory.Exists(expected))
            throw new DirectoryNotFoundException($"DEV formal storage root is unavailable: {expected}");
    }

    private static void RequireSeparateTechnicalRoot(string? actual, bool requireAccessible)
    {
        if (string.IsNullOrWhiteSpace(actual) || !Path.IsPathFullyQualified(actual))
            throw new InvalidOperationException("RawData:NasRootPath must be a fully-qualified technical NAS path.");
        var normalized = Normalize(actual);
        if (!IsAtOrBelow(normalized, ApprovedWorkspaceRoot) ||
            normalized.Equals(Normalize(ApprovedWorkspaceRoot), StringComparison.OrdinalIgnoreCase) ||
            IsAtOrBelow(normalized, Root) || IsAtOrBelow(Root, normalized))
            throw new InvalidOperationException(
                "DEV technical storage must be under the approved NAS workspace and outside the formal business root.");
        if (requireAccessible && !Directory.Exists(normalized))
            throw new DirectoryNotFoundException($"DEV technical storage root is unavailable: {normalized}");
    }

    private static string Normalize(string path) => Path.GetFullPath(path.Trim())
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
