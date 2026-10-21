using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Models.Config;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Services.Implementations;

public sealed class DocumentLibraryService(
    IDbContextFactory<AppDbContext> dbFactory,
    IOptions<DocumentStorageOptions> options,
    IOptions<DocumentStagingOptions> stagingOptions,
    IBusinessWriteAuthorizationGuard businessWrite) : IDocumentLibraryService
{
    public async Task<List<DocumentFolderDto>> GetFoldersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.DocumentFolders.AsNoTracking()
            .OrderBy(x => x.Level).ThenBy(x => x.SortOrder).ThenBy(x => x.Name)
            .Select(x => new DocumentFolderDto
            {
                Id = x.Id,
                ParentId = x.ParentId,
                Level = x.Level,
                Name = x.Name,
                Description = x.Description,
                SortOrder = x.SortOrder,
                DocumentCount = x.Files.Count
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<Guid> CreateFolderAsync(
        DocumentFolderUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        await businessWrite.DemandAsync(cancellationToken);
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var parent = request.ParentId is null
            ? null
            : await db.DocumentFolders.SingleOrDefaultAsync(x => x.Id == request.ParentId, cancellationToken)
              ?? throw new InvalidOperationException("找不到上層資料夾。");
        var name = DocumentLibraryRules.NormalizeName(request.Name);
        var level = DocumentLibraryRules.LevelForParent(parent?.Level);
        if (await db.DocumentFolders.AnyAsync(
                x => x.ParentId == request.ParentId && x.Name.ToLower() == name.ToLower(), cancellationToken))
            throw new InvalidOperationException("同一層已有相同名稱的資料夾。");

        var now = DateTime.UtcNow;
        var entity = new DocumentFolder
        {
            Id = Guid.NewGuid(),
            ParentId = parent?.Id,
            Level = level,
            Name = name,
            Description = NormalizeDescription(request.Description),
            SortOrder = request.SortOrder,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.DocumentFolders.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return entity.Id;
    }

    public async Task<bool> UpdateFolderAsync(
        Guid id,
        DocumentFolderUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        await businessWrite.DemandAsync(cancellationToken);
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.DocumentFolders.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null) return false;
        if (request.ParentId != entity.ParentId)
            throw new InvalidOperationException("建立後不可移動資料夾；可修改名稱、說明與排序。");
        var name = DocumentLibraryRules.NormalizeName(request.Name);
        if (await db.DocumentFolders.AnyAsync(
                x => x.Id != id && x.ParentId == entity.ParentId && x.Name.ToLower() == name.ToLower(), cancellationToken))
            throw new InvalidOperationException("同一層已有相同名稱的資料夾。");
        entity.Name = name;
        entity.Description = NormalizeDescription(request.Description);
        entity.SortOrder = request.SortOrder;
        entity.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<List<DocumentLibraryFileDto>> GetFilesAsync(
        Guid folderId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var folder = await db.DocumentFolders.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == folderId, cancellationToken)
            ?? throw new KeyNotFoundException("找不到資料夾。");
        if (folder.Level != DocumentLibraryRules.MaximumLevel)
            throw new InvalidOperationException("只有第三層資料夾可顯示文件清單。");
        return await db.DocumentLibraryFiles.AsNoTracking()
            .Where(x => x.FolderId == folderId)
            .OrderByDescending(x => x.UploadedAt)
            .Select(x => ToDto(x, x.ProcessingJob == null ? null : x.ProcessingJob.Status))
            .ToListAsync(cancellationToken);
    }

    public async Task<DocumentLibraryFileDto> UploadAsync(
        Guid folderId,
        IFormFile file,
        string uploadedBy,
        CancellationToken cancellationToken = default)
    {
        await businessWrite.DemandAsync(cancellationToken);
        ArgumentNullException.ThrowIfNull(file);
        if (file.Length <= 0) throw new InvalidOperationException("不可上傳空白檔案。");
        if (file.Length > options.Value.MaxFileSizeBytes)
            throw new InvalidOperationException($"檔案大小不可超過 {options.Value.MaxFileSizeBytes / 1_000_000} MB。");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var folder = await db.DocumentFolders.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == folderId, cancellationToken)
            ?? throw new KeyNotFoundException("找不到資料夾。");
        if (folder.Level != DocumentLibraryRules.MaximumLevel)
            throw new InvalidOperationException("文件只能上傳至第三層資料夾。");

        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var originalName = DocumentLibraryRules.NormalizeOriginalFileName(file.FileName);
        var standardName = DocumentLibraryRules.CreateStandardFileName(originalName, now, id);
        var relativePath = Path.Combine(now.ToString("yyyy"), now.ToString("MM"), folderId.ToString("N"), standardName);
        var absolutePath = ResolvePath(DocumentStorageAreas.Staging, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);

        string sha256;
        try
        {
            await using var source = file.OpenReadStream();
            await using var destination = new FileStream(
                absolutePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hash.AppendData(buffer, 0, read);
            }
            sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

            var entity = new DocumentLibraryFile
            {
                Id = id,
                FolderId = folderId,
                OriginalFileName = originalName,
                StandardFileName = standardName,
                ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? null : file.ContentType,
                UploadedBy = string.IsNullOrWhiteSpace(uploadedBy) ? "System" : uploadedBy.Trim(),
                UploadedAt = now,
                FileSize = file.Length,
                Sha256 = sha256,
                RelativePath = relativePath,
                StorageArea = DocumentStorageAreas.Staging
            };
            var job = new DocumentProcessingJob
            {
                Id = Guid.NewGuid(),
                DocumentFileId = entity.Id,
                Status = DocumentProcessingStatuses.Pending,
                AttemptCount = 0,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.DocumentLibraryFiles.Add(entity);
            db.DocumentProcessingJobs.Add(job);
            await db.SaveChangesAsync(cancellationToken);
            return ToDto(entity, job.Status);
        }
        catch
        {
            if (File.Exists(absolutePath)) File.Delete(absolutePath);
            throw;
        }
    }

    public async Task<DocumentDownload?> OpenDownloadAsync(
        Guid fileId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.DocumentLibraryFiles.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == fileId, cancellationToken);
        if (entity is null) return null;
        var path = ResolvePath(entity.StorageArea, entity.RelativePath);
        if (!File.Exists(path)) throw new FileNotFoundException("文件實體檔案不存在。", path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return new DocumentDownload(stream, entity.OriginalFileName, entity.ContentType);
    }

    private string ResolvePath(string storageArea, string relativePath)
    {
        var configuredRoot = storageArea switch
        {
            DocumentStorageAreas.Library => options.Value.RootPath?.Trim(),
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
            throw new InvalidOperationException("文件儲存路徑無效。");
        return fullPath;
    }

    private static string? NormalizeDescription(string? value)
    {
        var description = value?.Trim();
        if (string.IsNullOrEmpty(description)) return null;
        if (description.Length > 1000)
            throw new InvalidOperationException("資料夾說明不可超過 1000 個字元。");
        return description;
    }

    private static DocumentLibraryFileDto ToDto(DocumentLibraryFile entity, string? processingStatus) => new()
    {
        Id = entity.Id,
        FolderId = entity.FolderId,
        OriginalFileName = entity.OriginalFileName,
        StandardFileName = entity.StandardFileName,
        ContentType = entity.ContentType,
        UploadedBy = entity.UploadedBy,
        UploadedAt = entity.UploadedAt,
        FileSize = entity.FileSize,
        Sha256 = entity.Sha256,
        ProcessingStatus = processingStatus ?? string.Empty,
        ProcessingStatusDisplay = processingStatus switch
        {
            DocumentProcessingStatuses.Pending => "已接收／待 KM 處理",
            DocumentProcessingStatuses.Processing => "處理中",
            DocumentProcessingStatuses.Completed => "已完成",
            DocumentProcessingStatuses.Failed => "處理失敗",
            _ => "既有文件"
        }
    };
}
