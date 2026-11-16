using System.Text.RegularExpressions;

namespace SIT.DepartmentSystem.Web.Services;

public static partial class DocumentLibraryRules
{
    public const int MaximumLevel = 3;
    public const int MaximumStoredFileNameLength = 240;
    private static readonly HashSet<string> PreviewImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".heic"
    };
    private static readonly HashSet<string> PreviewImageContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/gif", "image/bmp", "image/webp", "image/tiff", "image/heic", "image/heif"
    };

    public static int LevelForParent(int? parentLevel)
    {
        var level = parentLevel is null ? 1 : parentLevel.Value + 1;
        if (level is < 1 or > MaximumLevel)
            throw new InvalidOperationException("文件資料夾最多只能建立三層。");
        return level;
    }

    public static string NormalizeName(string? value)
    {
        var name = value?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("資料夾名稱不可空白。");
        if (name.Length > 200)
            throw new InvalidOperationException("資料夾名稱不可超過 200 個字元。");
        return name;
    }

    public static string NormalizeOriginalFileName(string? value)
    {
        var fileName = Path.GetFileName(value?.Replace('\\', '/'))?.Trim();
        if (string.IsNullOrWhiteSpace(fileName))
            throw new InvalidOperationException("檔案名稱不可空白。");
        if (fileName.Length > 300)
            throw new InvalidOperationException("檔案名稱不可超過 300 個字元。");
        return fileName;
    }

    public static string CreateStandardFileName(string originalFileName, DateTime utcNow, Guid id)
    {
        _ = utcNow; // Kept for source compatibility with the existing staging call sites.
        var normalized = NormalizeOriginalFileName(originalFileName);
        var extension = Path.GetExtension(normalized);
        if (extension.Length > 20 || !SafeExtension().IsMatch(extension)) extension = string.Empty;
        extension = extension.ToLowerInvariant();

        var stem = extension.Length == 0 ? normalized : normalized[..^extension.Length];
        stem = InvalidStoredFileNameCharacters().Replace(stem, "_");
        stem = RepeatedWhitespace().Replace(stem, " ").Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(stem)) stem = "document";
        if (ReservedWindowsNames().IsMatch(stem)) stem = "_" + stem;

        var suffix = "~" + id.ToString("N")[..8];
        var maximumStemLength = MaximumStoredFileNameLength - suffix.Length - extension.Length;
        if (stem.Length > maximumStemLength)
            stem = stem[..maximumStemLength].TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(stem)) stem = "document";
        return stem + suffix + extension;
    }

    public static bool IsPreviewableImage(string? contentType, string fileName) =>
        (!string.IsNullOrWhiteSpace(contentType) && PreviewImageContentTypes.Contains(contentType.Trim())) ||
        PreviewImageExtensions.Contains(Path.GetExtension(fileName));

    [GeneratedRegex(@"^\.[A-Za-z0-9]+$")]
    private static partial Regex SafeExtension();

    [GeneratedRegex(@"[<>:\""/\\|?*\x00-\x1F]")]
    private static partial Regex InvalidStoredFileNameCharacters();

    [GeneratedRegex(@"\s+")]
    private static partial Regex RepeatedWhitespace();

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedWindowsNames();
}
