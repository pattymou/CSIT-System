using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Models.Config;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Services.Implementations;

public sealed class VerificationApplicationFileService(
    AppDbContext db,
    IOptions<DocumentStorageOptions> storageOptions,
    IOptions<DocumentStagingOptions> stagingOptions,
    IBusinessWriteAuthorizationGuard businessWrite) : IVerificationApplicationFileService
{
    public async Task<IReadOnlyList<VerificationApplicationFileDto>> GetAsync(
        Guid applicationId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var application = await db.VerificationApplications.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == applicationId, cancellationToken)
            ?? throw new KeyNotFoundException("找不到驗證申請單。");
        EnsureCanView(application, user);
        var files = await db.VerificationApplicationFiles.AsNoTracking()
            .Include(x => x.DocumentLibraryFile)
            .Where(x => x.VerificationApplicationId == applicationId)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
        return files.Select(x => ToDto(x, application.ProductModel)).ToList();
    }

    public async Task<VerificationApplicationFileDto> UploadAsync(
        Guid applicationId,
        IFormFile file,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        await businessWrite.DemandAsync(cancellationToken);
        ArgumentNullException.ThrowIfNull(file);
        var account = VerificationApplicationSecurity.GetAccount(user);
        var application = await db.VerificationApplications
            .SingleOrDefaultAsync(x => x.Id == applicationId, cancellationToken)
            ?? throw new KeyNotFoundException("找不到驗證申請單。");
        EnsureCanManage(application, account);
        if (file.Length <= 0) throw new InvalidOperationException("不可上傳空白檔案。");
        if (file.Length > storageOptions.Value.MaxFileSizeBytes)
            throw new InvalidOperationException(
                $"檔案大小不可超過 {storageOptions.Value.MaxFileSizeBytes / 1_000_000} MB。");

        var originalName = DocumentLibraryRules.NormalizeOriginalFileName(file.FileName);
        var normalizedName = originalName.ToLowerInvariant();
        if (await db.VerificationApplicationFiles.AsNoTracking().AnyAsync(
                x => x.VerificationApplicationId == applicationId &&
                     x.FileName.ToLower() == normalizedName,
                cancellationToken))
            throw DuplicateFileName(originalName);
        var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? null : file.ContentType.Trim();
        if (contentType?.Length > 200) throw new InvalidOperationException("檔案類型資訊過長。");

        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var standardName = DocumentLibraryRules.CreateStandardFileName(originalName, now, id);
        var relativePath = Path.Combine("verification-applications", applicationId.ToString("N"), standardName);
        var absolutePath = ResolvePath(DocumentStorageAreas.Staging, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);

        try
        {
            string sha256;
            await using (var source = file.OpenReadStream())
            await using (var destination = new FileStream(
                absolutePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long written = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    written += read;
                    if (written > file.Length)
                        throw new InvalidOperationException("上傳內容超過檔案宣告大小。");
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    hash.AppendData(buffer, 0, read);
                }
                await destination.FlushAsync(cancellationToken);
                if (written != file.Length)
                    throw new InvalidOperationException("上傳內容與檔案宣告大小不符。");
                sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }

            var entity = new VerificationApplicationFile
            {
                Id = id,
                VerificationApplicationId = applicationId,
                FileName = originalName,
                FilePath = relativePath,
                ContentType = contentType,
                FileSize = file.Length,
                Sha256 = sha256,
                UploadedBy = account,
                CreatedAt = now
            };
            db.VerificationApplicationFiles.Add(entity);
            application.RecordAttachmentChange(now);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                db.Entry(entity).State = EntityState.Detached;
                if (await db.VerificationApplicationFiles.AsNoTracking().AnyAsync(
                        x => x.VerificationApplicationId == applicationId &&
                             x.FileName.ToLower() == normalizedName,
                        cancellationToken))
                    throw DuplicateFileName(originalName);
                throw;
            }
            return ToDto(entity, application.ProductModel);
        }
        catch
        {
            if (File.Exists(absolutePath)) File.Delete(absolutePath);
            throw;
        }
    }

    public async Task<bool> DeleteAsync(
        Guid applicationId,
        Guid fileId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        await businessWrite.DemandAsync(cancellationToken);
        var account = VerificationApplicationSecurity.GetAccount(user);
        var application = await db.VerificationApplications
            .SingleOrDefaultAsync(x => x.Id == applicationId, cancellationToken)
            ?? throw new KeyNotFoundException("找不到驗證申請單。");
        EnsureCanManage(application, account);
        var file = await db.VerificationApplicationFiles
            .Include(x => x.DocumentLibraryFile)
            .SingleOrDefaultAsync(x => x.Id == fileId && x.VerificationApplicationId == applicationId, cancellationToken);
        if (file is null) return false;

        var storageArea = file.DocumentLibraryFile?.StorageArea ?? DocumentStorageAreas.Staging;
        var relativePath = file.DocumentLibraryFile?.RelativePath ?? file.FilePath;
        var absolutePath = ResolvePath(storageArea, relativePath);
        if (file.DocumentLibraryFile is not null) db.DocumentLibraryFiles.Remove(file.DocumentLibraryFile);
        db.VerificationApplicationFiles.Remove(file);
        application.RecordAttachmentChange(DateTime.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        if (File.Exists(absolutePath)) File.Delete(absolutePath);
        DeleteEmptyApplicationDirectory(absolutePath);
        return true;
    }

    public async Task<DocumentDownload?> OpenDownloadAsync(
        Guid fileId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var file = await db.VerificationApplicationFiles.AsNoTracking()
            .Include(x => x.VerificationApplication)
            .Include(x => x.DocumentLibraryFile)
            .SingleOrDefaultAsync(x => x.Id == fileId, cancellationToken);
        if (file is null) return null;
        EnsureCanView(file.VerificationApplication, user);
        var storageArea = file.DocumentLibraryFile?.StorageArea ?? DocumentStorageAreas.Staging;
        var relativePath = file.DocumentLibraryFile?.RelativePath ?? file.FilePath;
        var absolutePath = ResolvePath(storageArea, relativePath);
        if (!File.Exists(absolutePath)) throw new FileNotFoundException("附件實體檔案不存在。", absolutePath);
        var stream = new FileStream(
            absolutePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return new DocumentDownload(stream, file.FileName, file.ContentType);
    }

    private static void EnsureCanManage(VerificationApplication application, string account)
    {
        VerificationApplicationSecurity.EnsureApplicantOwnership(application, account);
        if (application.Status is not (VerificationApplicationStatus.Draft or VerificationApplicationStatus.Returned))
            throw new InvalidOperationException("只有草稿或已退回的申請可以修改附件。");
    }

    private static void EnsureCanView(VerificationApplication application, ClaimsPrincipal user)
    {
        var account = VerificationApplicationSecurity.GetAccount(user);
        if (string.Equals(application.ApplicantAccount, account, StringComparison.Ordinal)) return;
        if (application.Status != VerificationApplicationStatus.Draft &&
            user.HasClaim(SystemAuthorization.AccessScopeClaim, SystemAuthorization.AccessScopes.CsitStaff)) return;
        throw new UnauthorizedAccessException("您沒有權限查看此申請的附件。");
    }

    private string ResolvePath(string storageArea, string relativePath)
    {
        var configuredRoot = storageArea switch
        {
            DocumentStorageAreas.Library => storageOptions.Value.RootPath?.Trim(),
            DocumentStorageAreas.Staging => stagingOptions.Value.RootPath?.Trim(),
            _ => throw new InvalidOperationException("未知的文件儲存區域。")
        };
        if (string.IsNullOrWhiteSpace(configuredRoot))
            throw new InvalidOperationException(storageArea == DocumentStorageAreas.Staging
                ? "尚未設定 DocumentStaging:RootPath。"
                : "尚未設定 DocumentStorage:RootPath。");
        var root = Path.GetFullPath(configuredRoot);
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("附件儲存路徑無效。");
        return fullPath;
    }

    private void DeleteEmptyApplicationDirectory(string absolutePath)
    {
        var directory = Path.GetDirectoryName(absolutePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory) ||
            Directory.EnumerateFileSystemEntries(directory).Any()) return;
        var root = Path.GetFullPath(stagingOptions.Value.RootPath).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            Directory.Delete(directory);
    }

    private static VerificationApplicationFileDto ToDto(
        VerificationApplicationFile file,
        string? modelName) => new()
    {
        Id = file.Id,
        FileName = file.FileName,
        ContentType = file.ContentType,
        FileSize = file.FileSize,
        UploadedBy = file.UploadedBy,
        UploadedAt = file.CreatedAt,
        ModelName = modelName,
        DocumentFolderId = file.DocumentLibraryFile?.FolderId,
        DocumentLibraryFileId = file.DocumentLibraryFileId
    };

    private static InvalidOperationException DuplicateFileName(string fileName) =>
        new($"檔案已存在：{fileName}，請刪除舊檔或更名後再上傳。");
}
