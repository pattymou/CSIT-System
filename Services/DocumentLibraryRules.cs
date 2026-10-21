using System.Text.RegularExpressions;

namespace SIT.DepartmentSystem.Web.Services;

public static partial class DocumentLibraryRules
{
    public const int MaximumLevel = 3;

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
        var fileName = Path.GetFileName(value)?.Trim();
        if (string.IsNullOrWhiteSpace(fileName))
            throw new InvalidOperationException("檔案名稱不可空白。");
        if (fileName.Length > 300)
            throw new InvalidOperationException("檔案名稱不可超過 300 個字元。");
        return fileName;
    }

    public static string CreateStandardFileName(string originalFileName, DateTime utcNow, Guid id)
    {
        var extension = Path.GetExtension(originalFileName);
        if (extension.Length > 20 || !SafeExtension().IsMatch(extension)) extension = string.Empty;
        return $"DOC-{utcNow:yyyyMMddHHmmssfff}-{id:N}{extension.ToLowerInvariant()}";
    }

    [GeneratedRegex(@"^\.[A-Za-z0-9]+$")]
    private static partial Regex SafeExtension();
}
