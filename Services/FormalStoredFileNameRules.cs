using System.Text.RegularExpressions;

namespace SIT.DepartmentSystem.Web.Services;

/// <summary>Creates a readable, collision-safe file name for final formal storage.</summary>
public static partial class FormalStoredFileNameRules
{
    public const int MaximumFileNameLength = 240;

    public static string Create(string originalFileName, Guid stableId)
    {
        var normalized = DocumentLibraryRules.NormalizeOriginalFileName(originalFileName);
        var extension = Path.GetExtension(normalized);
        if (extension.Length > 20 || !SafeExtension().IsMatch(extension)) extension = string.Empty;
        extension = extension.ToLowerInvariant();

        var stem = extension.Length == 0 ? normalized : normalized[..^extension.Length];
        stem = InvalidFileNameCharacters().Replace(stem, "_");
        stem = RepeatedWhitespace().Replace(stem, " ").Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(stem)) stem = "document";
        if (ReservedWindowsNames().IsMatch(stem)) stem = "_" + stem;

        var suffix = "~" + stableId.ToString("N")[..8];
        var maximumStemLength = MaximumFileNameLength - suffix.Length - extension.Length;
        if (stem.Length > maximumStemLength)
            stem = stem[..maximumStemLength].TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(stem)) stem = "document";
        return stem + suffix + extension;
    }

    [GeneratedRegex(@"^\.[A-Za-z0-9]+$")]
    private static partial Regex SafeExtension();

    [GeneratedRegex(@"[<>:\""/\\|?*\x00-\x1F]")]
    private static partial Regex InvalidFileNameCharacters();

    [GeneratedRegex(@"\s+")]
    private static partial Regex RepeatedWhitespace();

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedWindowsNames();
}
