using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Models.Config;
using SIT.DepartmentSystem.Web.Services.Interfaces;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

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

    public async Task<SystemDocumentCatalogDto> GetSystemDocumentsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var equipmentFiles = await db.ApparatusFiles.AsNoTracking()
            .Where(x => x.Apparatus.ModuleCode.ToLower() == "equipment")
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new SystemDocumentFileDto
            {
                FileName = x.FileName,
                SourceModule = "設備管理",
                FileSize = x.FileSize,
                UploadedAt = x.CreatedAt,
                DownloadUrl = $"/api/assets/files/{x.Id}/content"
            })
            .ToListAsync(cancellationToken);

        var testReports = await db.ModuleCaseFiles.AsNoTracking()
            .Where(x => x.FileKind == ModuleCaseFileKinds.TestReport
                && x.ReviewStatus == TestReportReviewStatuses.Approved
                && x.IsFinal)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new SystemDocumentFileDto
            {
                FileName = x.FileName,
                SourceModule = "測試報告",
                FileSize = x.FileSize,
                UploadedAt = x.CreatedAt,
                DownloadUrl = $"/api/files/{x.Id}/download"
            })
            .ToListAsync(cancellationToken);

        return new SystemDocumentCatalogDto
        {
            Categories =
            [
                new()
                {
                    Code = "verification-applications",
                    Name = "驗證申請文件",
                    EmptyMessage = "目前正式系統尚無已保存且可沿用既有下載功能的驗證申請文件。"
                },
                new()
                {
                    Code = "equipment",
                    Name = "設備管理文件",
                    EmptyMessage = "目前沒有可供查閱的設備管理文件。",
                    Files = equipmentFiles
                },
                new()
                {
                    Code = "test-reports",
                    Name = "測試報告",
                    EmptyMessage = "目前沒有已核准的正式測試報告。",
                    Files = testReports
                }
            ]
        };
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

        if (entity.Level == 1)
        {
            if (request.ParentId is not null)
                throw new InvalidOperationException("L1 必須維持根層，不可移至其他資料夾下。");
        }
        else
        {
            if (request.ParentId is null)
                throw new InvalidOperationException($"L{entity.Level} 必須選擇 L{entity.Level - 1} 作為上層資料夾。");
            var newParent = await db.DocumentFolders.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == request.ParentId, cancellationToken)
                ?? throw new InvalidOperationException("找不到要移入的上層資料夾。");
            if (newParent.Level != entity.Level - 1)
                throw new InvalidOperationException($"L{entity.Level} 只能移至 L{entity.Level - 1} 資料夾下。");

            var parentLinks = await db.DocumentFolders.AsNoTracking()
                .Select(x => new { x.Id, x.ParentId })
                .ToDictionaryAsync(x => x.Id, x => x.ParentId, cancellationToken);
            var ancestorId = request.ParentId;
            while (ancestorId is not null)
            {
                if (ancestorId == entity.Id)
                    throw new InvalidOperationException("不可將資料夾移至自己或自己的下層，避免形成循環。");
                ancestorId = parentLinks.TryGetValue(ancestorId.Value, out var parentId) ? parentId : null;
            }
        }

        var name = DocumentLibraryRules.NormalizeName(request.Name);
        if (await db.DocumentFolders.AnyAsync(
                x => x.Id != id && x.ParentId == request.ParentId && x.Name.ToLower() == name.ToLower(), cancellationToken))
            throw new InvalidOperationException("同一層已有相同名稱的資料夾。");
        entity.ParentId = request.ParentId;
        entity.Name = name;
        entity.Description = NormalizeDescription(request.Description);
        entity.SortOrder = request.SortOrder;
        entity.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteFolderAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await businessWrite.DemandAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.DocumentFolders.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null) return false;

        if (await db.DocumentFolders.AnyAsync(x => x.ParentId == id, cancellationToken))
            throw new InvalidOperationException("此資料夾仍有子資料夾，請先刪除所有下層資料夾。");
        if (await db.DocumentLibraryFiles.AnyAsync(x => x.FolderId == id, cancellationToken))
            throw new InvalidOperationException("此資料夾仍有文件紀錄（包含待 KM 處理的文件），無法刪除。");

        db.DocumentFolders.Remove(entity);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            throw new InvalidOperationException("資料夾已新增下層資料或文件，請重新整理後再試。");
        }
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
        var files = await db.DocumentLibraryFiles.AsNoTracking()
            .Include(x => x.ProcessingJob)
            .Where(x => x.FolderId == folderId)
            .OrderByDescending(x => x.UploadedAt)
            .ToListAsync(cancellationToken);
        var jobIds = files.Where(x => x.ProcessingJob is not null)
            .Select(x => x.ProcessingJob!.Id).ToList();
        var kmJobs = await db.KmIntegrationOutbox.AsNoTracking()
            .Where(x => jobIds.Contains(x.EventId) &&
                x.EventType == KmIntegrationEventTypes.DocumentLibraryFileUploaded)
            .ToDictionaryAsync(x => x.EventId, x => x.KmJobId, cancellationToken);
        return files.Select(x => ToDto(x, x.ProcessingJob,
            x.ProcessingJob is not null && kmJobs.TryGetValue(x.ProcessingJob.Id, out var kmJobId)
                ? kmJobId : null)).ToList();
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
            db.KmIntegrationOutbox.Add(KmIntegrationOutbox.ForDocumentLibraryFile(entity, job, now));
            await db.SaveChangesAsync(cancellationToken);
            return ToDto(entity, job, null);
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
        var linkedRecordId = await db.VerificationApplicationFiles.AsNoTracking()
            .Where(x => x.DocumentLibraryFileId == fileId)
            .Select(x => x.VerificationApplication.ModuleRecordId)
            .SingleOrDefaultAsync(cancellationToken);
        if (linkedRecordId is { } recordId &&
            await VerificationCaseDeletionHold.IsActiveAsync(db, recordId, cancellationToken)) return null;
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

    private static DocumentLibraryFileDto ToDto(
        DocumentLibraryFile entity,
        DocumentProcessingJob? job,
        string? kmJobId) => new()
    {
        Id = entity.Id,
        CsitJobId = job?.Id,
        KmJobId = kmJobId,
        FolderId = entity.FolderId,
        OriginalFileName = entity.OriginalFileName,
        StandardFileName = entity.StandardFileName,
        ContentType = entity.ContentType,
        UploadedBy = entity.UploadedBy,
        UploadedAt = entity.UploadedAt,
        FileSize = entity.FileSize,
        Sha256 = entity.Sha256,
        ProcessingStatus = job?.Status ?? string.Empty,
        ArchiveStatus = job?.ArchiveStatus ?? string.Empty,
        ProcessingError = job?.KmErrorMessage ?? job?.LastError,
        ArchiveError = job?.ArchiveLastError,
        MetadataAvailable = !string.IsNullOrWhiteSpace(job?.MetadataJson),
        ProcessingStatusCssClass = job?.Status switch
        {
            DocumentProcessingStatuses.Pending => "pending",
            DocumentProcessingStatuses.Accepted or DocumentProcessingStatuses.Processing => "processing",
            DocumentProcessingStatuses.Completed when job.ArchiveStatus == DocumentArchiveStatuses.Failed => "failed",
            DocumentProcessingStatuses.Completed when job.ArchiveStatus != DocumentArchiveStatuses.Completed => "processing",
            DocumentProcessingStatuses.Completed => "completed",
            DocumentProcessingStatuses.Failed => "failed",
            _ => string.Empty
        },
        ProcessingStatusDisplay = job?.Status switch
        {
            DocumentProcessingStatuses.Pending when !string.IsNullOrWhiteSpace(job.LastError) => "KM 派送失敗／待重試",
            DocumentProcessingStatuses.Pending => "已接收／待 KM 處理",
            DocumentProcessingStatuses.Accepted => "KM 已接受／等待處理",
            DocumentProcessingStatuses.Processing => "KM 處理中",
            DocumentProcessingStatuses.Completed when job.ArchiveStatus == DocumentArchiveStatuses.Pending =>
                "AI 完成／待 NAS 歸檔",
            DocumentProcessingStatuses.Completed when job.ArchiveStatus == DocumentArchiveStatuses.Archiving =>
                "NAS 歸檔中",
            DocumentProcessingStatuses.Completed when job.ArchiveStatus == DocumentArchiveStatuses.Failed =>
                "NAS 歸檔失敗",
            DocumentProcessingStatuses.Completed when job.ArchiveStatus == DocumentArchiveStatuses.Completed =>
                "已完成／NAS 已歸檔",
            DocumentProcessingStatuses.Completed => "AI 已完成",
            DocumentProcessingStatuses.Failed => "KM 處理失敗",
            _ => "既有文件"
        }
    };
}
