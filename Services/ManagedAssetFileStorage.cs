using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;

namespace SIT.DepartmentSystem.Web.Services;

public sealed class ManagedAssetFileStorage(AppDbContext db)
{
    public const long MaxFileSize = 200L * 1024 * 1024;
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".tif", ".tiff",
        ".txt", ".csv", ".json", ".xml", ".zip", ".7z"
    };

    public static string ValidateOriginalFileName(string? value, long length)
    {
        if (length <= 0) throw new InvalidOperationException("檔案不可為空白。");
        if (length > MaxFileSize) throw new InvalidOperationException("單一檔案不可超過 200 MB。");
        var name = Path.GetFileName(value ?? string.Empty).Trim();
        if (name.Length is 0 or > 300 || name != value)
            throw new InvalidOperationException("檔名無效或包含路徑字元。");
        var extension = Path.GetExtension(name);
        if (!AllowedExtensions.Contains(extension))
            throw new InvalidOperationException($"不允許的檔案格式：{extension}");
        return name;
    }

    public static bool IsImage(string? contentType, string fileName) =>
        contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true ||
        new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".tif", ".tiff" }
            .Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    public static string SafeFolderName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\']).ToHashSet();
        var cleaned = new string(value.Trim().Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray());
        cleaned = string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ');
        if (string.IsNullOrWhiteSpace(cleaned)) throw new InvalidOperationException("無法由名稱建立安全的歸檔資料夾。");
        return cleaned.Length <= 240 ? cleaned : cleaned[..240];
    }

    public async Task<string> EquipmentFolderAsync(string stableFolderName, CancellationToken cancellationToken = default) =>
        SafeCombine(await RootAsync(cancellationToken), "設備儀器管理", "設備資訊", stableFolderName);

    public async Task<string> VendorFolderAsync(string stableFolderName, CancellationToken cancellationToken = default) =>
        SafeCombine(await RootAsync(cancellationToken), "設備儀器管理", "廠商資訊", stableFolderName);

    public static string StoredFileName(Guid id, string originalFileName) =>
        id.ToString("N") + Path.GetExtension(originalFileName).ToLowerInvariant();

    public async Task<string> ArchiveAsync(Stream source, string folder, string storedFileName,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(folder);
        var finalPath = SafeCombine(folder, storedFileName);
        var pendingPath = finalPath + ".pending";
        if (File.Exists(finalPath))
        {
            await using var incoming = new MemoryStream();
            await source.CopyToAsync(incoming, cancellationToken);
            incoming.Position = 0;
            await using var existing = File.OpenRead(finalPath);
            var incomingHash = await SHA256.HashDataAsync(incoming, cancellationToken);
            var existingHash = await SHA256.HashDataAsync(existing, cancellationToken);
            if (!incomingHash.AsSpan().SequenceEqual(existingHash))
                throw new InvalidOperationException("相同文件 ID 的既有檔案內容不同，已停止覆蓋。");
            return Convert.ToHexString(existingHash);
        }

        await using (var destination = new FileStream(pendingPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await source.CopyToAsync(destination, cancellationToken);
            await destination.FlushAsync(cancellationToken);
        }
        File.Move(pendingPath, finalPath, false);

        await using var verification = File.OpenRead(finalPath);
        return Convert.ToHexString(await SHA256.HashDataAsync(verification, cancellationToken));
    }

    public static string FullPath(string folder, string storedFileName) => SafeCombine(folder, storedFileName);

    private async Task<string> RootAsync(CancellationToken cancellationToken)
    {
        var root = await db.SystemOptions.AsNoTracking()
            .Where(x => x.Category == "AssetRootPath" && x.IsEnabled && x.Name.ToLower() == "equipment")
            .Select(x => x.Value).FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("找不到受管理的設備 NAS Root 設定（AssetRootPath/equipment）。");
        return Path.GetFullPath(root.Trim());
    }

    private static string SafeCombine(string root, params string[] segments)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine([fullRoot, .. segments]));
        if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("歸檔路徑超出受管理的 NAS Root。");
        return path;
    }
}
