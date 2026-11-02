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
        var folders = await db.DocumentFolders.AsNoTracking()
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
        var systemIds = SystemManagedFolderIds(folders);
        foreach (var folder in folders) folder.IsSystemManaged = systemIds.Contains(folder.Id);
        return folders;
    }

    public async Task<SystemDocumentCatalogDto> GetSystemDocumentsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var testReports = await db.ModuleCaseFiles.AsNoTracking().Include(x => x.Task)
            .Where(x => x.FileKind == ModuleCaseFileKinds.TestReport
                && x.ReviewStatus == TestReportReviewStatuses.Approved
                && x.IsFinal && x.IsRawDataExported)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);

        var applicationModels = await db.VerificationApplications.AsNoTracking()
            .Where(x => x.ModuleRecordId.HasValue && !string.IsNullOrWhiteSpace(x.ProductModel))
            .ToDictionaryAsync(x => x.ModuleRecordId!.Value, x => x.ProductModel!, cancellationToken);
        var applicationFiles = await db.VerificationApplicationFiles.AsNoTracking()
            .Include(x => x.VerificationApplication)
            .Include(x => x.DocumentLibraryFile)
            .Where(x => x.DocumentLibraryFileId.HasValue && x.DocumentLibraryFile != null &&
                x.DocumentLibraryFile.StorageArea == DocumentStorageAreas.Library &&
                x.VerificationApplication.Status == VerificationApplicationStatus.Accepted &&
                !string.IsNullOrWhiteSpace(x.VerificationApplication.ProductModel))
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
        var productInformationFiles = await db.DocumentLibraryFiles.AsNoTracking()
            .Include(x => x.Folder).ThenInclude(x => x.Parent).ThenInclude(x => x!.Parent)
            .Include(x => x.ProcessingJob)
            .Where(x => x.Folder.Name == "產品資訊" && x.Folder.Parent != null &&
                x.Folder.Parent.Parent != null && x.Folder.Parent.Parent.Name == "產品驗證資料")
            .OrderByDescending(x => x.UploadedAt)
            .ToListAsync(cancellationToken);
        var equipmentFiles = await db.ApparatusFiles.AsNoTracking().Include(x => x.Apparatus)
            .Where(x => x.Apparatus.ModuleCode.ToLower() == "equipment" &&
                x.ArchiveStatus == FileArchiveStatuses.Archived)
            .OrderByDescending(x => x.CreatedAt).ToListAsync(cancellationToken);
        var vendorFiles = await db.VendorFiles.AsNoTracking().Include(x => x.Vendor)
            .Where(x => x.ArchiveStatus == FileArchiveStatuses.Archived)
            .OrderByDescending(x => x.UploadedAt).ToListAsync(cancellationToken);

        var folders = new List<SystemDocumentFolderDto>();
        var modelNames = testReports.Select(x => ModelNameFor(x, applicationModels))
            .Concat(applicationFiles.Select(x => x.VerificationApplication.ProductModel))
            .Concat(productInformationFiles.Select(x => x.Folder.Parent!.Name))
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        if (modelNames.Count > 0)
        {
            const string rootKey = "verification";
            folders.Add(SystemFolder(rootKey, null, 1, "產品驗證資料", "驗證申請與正式成果文件。", 10));
            foreach (var modelName in modelNames)
            {
                var modelKey = SystemKey("verification-model", modelName);
                folders.Add(SystemFolder(modelKey, rootKey, 2, modelName, $"Model Name：{modelName}", 10));

                var reportFiles = testReports.Where(x => string.Equals(
                        ModelNameFor(x, applicationModels), modelName, StringComparison.OrdinalIgnoreCase))
                    .Select(x => new SystemDocumentFileDto
                    {
                        SourceKey = $"test-report:{x.Id:N}", FileName = x.FileName,
                        SourceModule = "測試報告", ContentType = x.ContentType,
                        IsImage = IsImage(x.ContentType, x.FileName), FileSize = x.FileSize,
                        UploadedAt = x.CreatedAt, ProcessingStatusDisplay = "正式入庫完成",
                        ProcessingStatusCssClass = "completed", DownloadUrl = $"/api/files/{x.Id}/download",
                        PreviewUrl = $"/api/files/{x.Id}/download"
                    }).ToList();
                folders.Add(SystemFolder($"{modelKey}:reports", modelKey, 3, "測試報告",
                    "已核准、已正式完成的測試報告。", 10, reportFiles));

                var productFiles = productInformationFiles.Where(x => string.Equals(
                        x.Folder.Parent!.Name, modelName, StringComparison.OrdinalIgnoreCase))
                    .Select(ToSystemFile).ToList();
                folders.Add(SystemFolder($"{modelKey}:product", modelKey, 3, "產品資訊",
                    "文件專區直接上傳的產品資訊。", 20, productFiles, canUpload: true,
                    uploadModelName: modelName));

                var linkedFiles = applicationFiles.Where(x => string.Equals(
                        x.VerificationApplication.ProductModel, modelName, StringComparison.OrdinalIgnoreCase))
                    .Select(x => new SystemDocumentFileDto
                    {
                        SourceKey = $"application-document:{x.DocumentLibraryFileId!.Value:N}", FileName = x.FileName,
                        SourceModule = "驗證申請", ContentType = x.ContentType,
                        IsImage = IsImage(x.ContentType, x.FileName), FileSize = x.FileSize,
                        UploadedAt = x.CreatedAt, ProcessingStatusDisplay = "正式入庫完成",
                        ProcessingStatusCssClass = "completed",
                        DownloadUrl = $"/api/document-library/files/{x.DocumentLibraryFileId}/download",
                        PreviewUrl = $"/api/document-library/files/{x.DocumentLibraryFileId}/preview"
                    }).ToList();
                folders.Add(SystemFolder($"{modelKey}:applications", modelKey, 3, "申請單文件",
                    "主管核准且正式歸檔完成的申請單附件。", 30, linkedFiles));
            }
        }

        if (equipmentFiles.Count > 0 || vendorFiles.Count > 0)
        {
            const string rootKey = "managed-assets";
            const string equipmentKey = "managed-assets:equipment";
            const string vendorKey = "managed-assets:vendors";
            folders.Add(SystemFolder(rootKey, null, 1, "設備儀器管理", "設備與廠商正式附件。", 20));
            folders.Add(SystemFolder(equipmentKey, rootKey, 2, "設備資訊", "設備正式附件與照片。", 10));
            folders.Add(SystemFolder(vendorKey, rootKey, 2, "廠商資訊", "廠商正式附件。", 20));
            foreach (var group in equipmentFiles.GroupBy(x => x.ApparatusId).OrderBy(x => x.First().Apparatus.Name))
            {
                var equipment = group.First().Apparatus;
                var name = EquipmentFolderName(equipment);
                var files = group.Select(x => new SystemDocumentFileDto
                {
                    SourceKey = $"equipment:{x.Id:N}", FileName = x.OriginalFileName ?? x.FileName,
                    SourceModule = "設備管理", ContentType = x.ContentType,
                    IsImage = IsImage(x.ContentType, x.OriginalFileName ?? x.FileName), FileSize = x.FileSize,
                    UploadedAt = x.CreatedAt, ProcessingStatusDisplay = "正式入庫完成",
                    ProcessingStatusCssClass = "completed", DownloadUrl = $"/api/assets/files/{x.Id}/content",
                    PreviewUrl = $"/api/assets/files/{x.Id}/image"
                }).ToList();
                folders.Add(SystemFolder(SystemKey("equipment", group.Key), equipmentKey, 3, name,
                    $"設備 ID：{group.Key}", 10, files));
            }
            foreach (var group in vendorFiles.GroupBy(x => x.VendorId).OrderBy(x => x.First().Vendor.Name))
            {
                var vendor = group.First().Vendor;
                var files = group.Select(x => new SystemDocumentFileDto
                {
                    SourceKey = $"vendor:{x.Id:N}", FileName = x.OriginalFileName,
                    SourceModule = "廠商管理", ContentType = x.ContentType,
                    IsImage = IsImage(x.ContentType, x.OriginalFileName), FileSize = x.FileSize,
                    UploadedAt = x.UploadedAt, ProcessingStatusDisplay = "正式入庫完成",
                    ProcessingStatusCssClass = "completed", DownloadUrl = $"/api/vendors/files/{x.Id}/content",
                    PreviewUrl = $"/api/vendors/files/{x.Id}/content"
                }).ToList();
                folders.Add(SystemFolder(SystemKey("vendor", group.Key.ToString("N")), vendorKey, 3,
                    vendor.Name, $"Vendor ID：{group.Key}", 10, files));
            }
        }

        foreach (var folder in folders.OrderByDescending(x => x.Level))
            folder.DocumentCount = folder.Level == 3 ? folder.Files.Select(x => x.SourceKey).Distinct().Count() :
                folders.Where(x => x.ParentKey == folder.Key).Sum(x => x.DocumentCount);
        return new SystemDocumentCatalogDto { Folders = folders };
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
        if (request.ParentId is null && IsReservedSystemRoot(request.Name))
            throw new InvalidOperationException("此名稱保留給系統文件分類，請使用其他自訂資料夾名稱。");
        if (parent is not null && await IsSystemManagedFolderAsync(db, parent.Id, cancellationToken))
            throw new InvalidOperationException("系統分類的固定層級不可由資料夾管理功能新增或調整。");
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
        if (await IsSystemManagedFolderAsync(db, id, cancellationToken))
            throw new InvalidOperationException("系統管理的分類資料夾不可更名、移動或修改。");

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
        if (await IsSystemManagedFolderAsync(db, id, cancellationToken))
            throw new InvalidOperationException("系統管理的分類資料夾不可刪除。");

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
        if (await IsSystemManagedFolderAsync(db, folderId, cancellationToken) &&
            !await IsProductInformationFolderAsync(db, folderId, cancellationToken))
            throw new InvalidOperationException("系統來源文件為唯讀，請回原來源模組管理附件。");

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

    public async Task<DocumentLibraryFileDto> UploadProductInformationAsync(
        string modelName,
        IFormFile file,
        string uploadedBy,
        CancellationToken cancellationToken = default)
    {
        await businessWrite.DemandAsync(cancellationToken);
        var normalizedModel = DocumentLibraryRules.NormalizeName(modelName);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var lowered = normalizedModel.ToLower();
        var hasSource = await db.VerificationApplications.AsNoTracking().AnyAsync(x =>
                x.ModuleRecordId.HasValue && x.ProductModel != null && x.ProductModel.ToLower() == lowered,
                cancellationToken)
            || await db.ModuleCaseFiles.AsNoTracking().AnyAsync(x =>
                x.FileKind == ModuleCaseFileKinds.TestReport && x.ReviewStatus == TestReportReviewStatuses.Approved &&
                x.IsFinal && x.IsRawDataExported && x.Task != null && x.Task.ModelName != null &&
                x.Task.ModelName.ToLower() == lowered, cancellationToken)
            || await db.DocumentFolders.AsNoTracking().AnyAsync(x => x.Level == 2 &&
                x.Name.ToLower() == lowered && x.Parent != null && x.Parent.Name == "產品驗證資料",
                cancellationToken);
        if (!hasSource) throw new InvalidOperationException("找不到可安全對應的 Model Name，未建立文件分類。");

        var now = DateTime.UtcNow;
        var root = await GetOrCreateFolderAsync(db, null, 1, "產品驗證資料",
            "驗證申請與驗證成果文件。", 10, now, cancellationToken);
        var model = await GetOrCreateFolderAsync(db, root.Id, 2, normalizedModel,
            $"Model Name：{normalizedModel}", 10, now, cancellationToken);
        var product = await GetOrCreateFolderAsync(db, model.Id, 3, "產品資訊",
            "文件專區直接上傳的產品資訊。", 20, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await UploadAsync(product.Id, file, uploadedBy, cancellationToken);
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

    private static string? ModelNameFor(ModuleCaseFile file, IReadOnlyDictionary<Guid, string> applicationModels)
    {
        if (!string.IsNullOrWhiteSpace(file.Task?.ModelName)) return file.Task.ModelName.Trim();
        return applicationModels.TryGetValue(file.RecordId, out var model) && !string.IsNullOrWhiteSpace(model)
            ? model.Trim()
            : null;
    }

    private static string EquipmentFolderName(Apparatus equipment)
    {
        var assetNumber = string.IsNullOrWhiteSpace(equipment.Number) ? equipment.Id : equipment.Number.Trim();
        return $"{equipment.Name}＋{assetNumber}";
    }

    private static bool IsImage(string? contentType, string fileName) =>
        DocumentLibraryRules.IsPreviewableImage(contentType, fileName);

    private static string SystemKey(string prefix, string value)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value.Trim().ToUpperInvariant()));
        return $"{prefix}:{Convert.ToHexString(hash.AsSpan(0, 10)).ToLowerInvariant()}";
    }

    private static SystemDocumentFolderDto SystemFolder(
        string key,
        string? parentKey,
        int level,
        string name,
        string description,
        int sortOrder,
        List<SystemDocumentFileDto>? files = null,
        bool canUpload = false,
        string? uploadModelName = null) => new()
    {
        Key = key,
        ParentKey = parentKey,
        Level = level,
        Name = name,
        Description = description,
        SortOrder = sortOrder,
        CanUpload = canUpload,
        UploadModelName = uploadModelName,
        Files = files?.DistinctBy(x => x.SourceKey).ToList() ?? []
    };

    private static SystemDocumentFileDto ToSystemFile(DocumentLibraryFile file)
    {
        var dto = ToDto(file, file.ProcessingJob, null);
        var download = $"/api/document-library/files/{file.Id}/download";
        return new SystemDocumentFileDto
        {
            SourceKey = $"document-library:{file.Id:N}",
            FileName = file.OriginalFileName,
            SourceModule = "文件專區",
            ContentType = file.ContentType,
            IsImage = IsImage(file.ContentType, file.OriginalFileName),
            FileSize = file.FileSize,
            UploadedAt = file.UploadedAt,
            ProcessingStatusDisplay = dto.ProcessingStatusDisplay,
            ProcessingStatusCssClass = dto.ProcessingStatusCssClass,
            DownloadUrl = download,
            PreviewUrl = $"/api/document-library/files/{file.Id}/preview"
        };
    }

    private static bool IsReservedSystemRoot(string? name) =>
        string.Equals(name?.Trim(), "產品驗證資料", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name?.Trim(), "設備儀器管理", StringComparison.OrdinalIgnoreCase);

    private static HashSet<Guid> SystemManagedFolderIds(IReadOnlyCollection<DocumentFolderDto> folders)
    {
        var result = folders.Where(x => x.ParentId is null && IsReservedSystemRoot(x.Name))
            .Select(x => x.Id).ToHashSet();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var folder in folders.Where(x => x.ParentId.HasValue && result.Contains(x.ParentId.Value)))
                changed |= result.Add(folder.Id);
        }
        return result;
    }

    private static async Task<bool> IsSystemManagedFolderAsync(
        AppDbContext db,
        Guid folderId,
        CancellationToken cancellationToken)
    {
        var folders = await db.DocumentFolders.AsNoTracking().Select(x => new DocumentFolderDto
        {
            Id = x.Id, ParentId = x.ParentId, Level = x.Level, Name = x.Name
        }).ToListAsync(cancellationToken);
        return SystemManagedFolderIds(folders).Contains(folderId);
    }

    private static Task<bool> IsProductInformationFolderAsync(
        AppDbContext db,
        Guid folderId,
        CancellationToken cancellationToken) => db.DocumentFolders.AsNoTracking().AnyAsync(x =>
            x.Id == folderId && x.Level == 3 && x.Name == "產品資訊" && x.Parent != null &&
            x.Parent.Level == 2 && x.Parent.Parent != null && x.Parent.Parent.Level == 1 &&
            x.Parent.Parent.Name == "產品驗證資料", cancellationToken);

    private static async Task<DocumentFolder> GetOrCreateFolderAsync(
        AppDbContext db,
        Guid? parentId,
        int level,
        string name,
        string description,
        int sortOrder,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var normalized = DocumentLibraryRules.NormalizeName(name);
        var tracked = db.ChangeTracker.Entries<DocumentFolder>().Select(x => x.Entity)
            .SingleOrDefault(x => x.ParentId == parentId &&
                string.Equals(x.Name, normalized, StringComparison.OrdinalIgnoreCase));
        if (tracked is not null) return tracked;
        var lowered = normalized.ToLower();
        var existing = await db.DocumentFolders.SingleOrDefaultAsync(x =>
            x.ParentId == parentId && x.Name.ToLower() == lowered, cancellationToken);
        if (existing is not null)
        {
            if (existing.Level != level)
                throw new InvalidOperationException($"系統文件資料夾「{normalized}」的層級不正確。");
            return existing;
        }
        var folder = new DocumentFolder
        {
            Id = Guid.NewGuid(), ParentId = parentId, Level = level, Name = normalized,
            Description = description, SortOrder = sortOrder, CreatedAt = now, UpdatedAt = now
        };
        db.DocumentFolders.Add(folder);
        return folder;
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
