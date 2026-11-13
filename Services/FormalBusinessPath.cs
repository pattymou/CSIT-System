using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Services;

public static class FormalBusinessPath
{
    public const string ProductVerification = "產品驗證資料";
    public const string ApplicationDocuments = "申請單文件";
    public const string TestReports = "測試報告";
    public const string ProductInformation = "產品資訊";
    public const string EquipmentManagement = "設備儀器管理";
    public const string EquipmentInformation = "設備資訊";
    public const string VendorInformation = "廠商資訊";
    public const string GeneralDocuments = "一般文件";

    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string Segment(string? value, Guid? stableId = null)
    {
        var original = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(original))
            throw new InvalidOperationException("正式歸檔資料夾名稱不可空白。");

        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\']).ToHashSet();
        var cleaned = new string(original.Select(character =>
            invalid.Contains(character) || char.IsControl(character) ? '_' : character).ToArray());
        cleaned = string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Trim('.', ' ');
        if (ReservedWindowsNames.Contains(cleaned)) cleaned = "_" + cleaned;
        if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "_";

        var changed = !string.Equals(cleaned, original, StringComparison.Ordinal);
        var suffix = changed && stableId.HasValue ? "~" + stableId.Value.ToString("N")[..8] : string.Empty;
        if (cleaned.Length + suffix.Length > 120)
        {
            suffix = stableId.HasValue ? "~" + stableId.Value.ToString("N")[..8] : suffix;
            cleaned = cleaned[..(120 - suffix.Length)].TrimEnd('.', ' ');
        }
        return cleaned + suffix;
    }

    public static string DocumentFolder(DocumentFolder leaf)
    {
        if (leaf.Level != DocumentLibraryRules.MaximumLevel || leaf.Parent?.Level != 2 ||
            leaf.Parent.Parent?.Level != 1)
            throw new InvalidOperationException("正式文件必須有完整且有效的 Level 1／2／3 分類。");

        return ThreeLevel(
            Segment(leaf.Parent.Parent.Name, leaf.Parent.Parent.Id),
            Segment(leaf.Parent.Name, leaf.Parent.Id),
            Segment(leaf.Name, leaf.Id));
    }

    public static string ProductVerificationFolder(string modelName, string category) =>
        ThreeLevel(ProductVerification, Segment(modelName), Segment(category));

    public static string EquipmentFolder(string propertyNumber, string equipmentName, Guid? stableId = null) =>
        Segment($"{propertyNumber.Trim()} - {equipmentName.Trim()}", stableId);

    public static string VendorFolder(string vendorName, Guid? stableId = null) =>
        Segment(vendorName, stableId);

    public static string ThreeLevel(string level1, string level2, string level3) =>
        Path.Combine(Segment(level1), Segment(level2), Segment(level3));
}
