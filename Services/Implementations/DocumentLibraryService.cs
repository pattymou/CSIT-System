using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Models.Config;
using SIT.DepartmentSystem.Web.Services.Interfaces;
using SIT.DepartmentSystem.Web.Services.KmIntegration;
using SIT.DepartmentSystem.Web.Services.Approval;

namespace SIT.DepartmentSystem.Web.Services.Implementations;

public sealed class DocumentLibraryService(
    IDbContextFactory<AppDbContext> dbFactory,
    IOptions<DocumentStorageOptions> options,
    IOptions<DocumentStagingOptions> stagingOptions,
    IBusinessWriteAuthorizationGuard businessWrite,
    IOptions<KmIntegrationOptions>? kmOptions = null) : IDocumentLibraryService, IDocumentLibraryFormalUploadService
{
    internal const int MaximumArchiveStatusBatchSize = 100;
    private readonly KmIntegrationOptions configuredKm = kmOptions?.Value ?? new KmIntegrationOptions();
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
        var reportRecordIds = testReports.Select(x => x.RecordId).Distinct().ToArray();
        var reportModuleCodes = await db.ModuleRecords.AsNoTracking()
            .Where(x => reportRecordIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Module.Code })
            .ToDictionaryAsync(x => x.Id, x => x.Code, cancellationToken);

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
        var productFileIds = productInformationFiles.Select(x => x.Id).ToArray();
        var productDeletionJobs = productFileIds.Length == 0
            ? new Dictionary<Guid, DocumentLibraryFileDeletionJob>()
            : await db.DocumentLibraryFileDeletionJobs.AsNoTracking()
                .Where(x => productFileIds.Contains(x.DocumentFileId))
                .ToDictionaryAsync(x => x.DocumentFileId, cancellationToken);
        var equipmentFiles = await db.ApparatusFiles.AsNoTracking().Include(x => x.Apparatus)
            .Where(x => x.Apparatus.ModuleCode.ToLower() == "equipment" &&
                x.ArchiveStatus == FileArchiveStatuses.Archived)
            .OrderByDescending(x => x.CreatedAt).ToListAsync(cancellationToken);
        var vendorFiles = await db.VendorFiles.AsNoTracking().Include(x => x.Vendor)
            .Where(x => x.ArchiveStatus == FileArchiveStatuses.Archived)
            .OrderByDescending(x => x.UploadedAt).ToListAsync(cancellationToken);
        var sourceIds = testReports.Select(x => x.Id)
            .Concat(applicationFiles.Select(x => x.Id))
            .Concat(equipmentFiles.Select(x => x.Id))
            .Concat(vendorFiles.Select(x => x.Id)).Distinct().ToArray();
        var sourceKm = sourceIds.Length == 0
            ? new Dictionary<(string Type, Guid Id), KmIntegrationOutbox>()
            : (await db.KmIntegrationOutbox.AsNoTracking()
                .Where(x => sourceIds.Contains(x.SourceEntityId))
                .ToListAsync(cancellationToken))
                .GroupBy(x => (x.SourceEntityType, x.SourceEntityId))
                .ToDictionary(x => x.Key, x => x.OrderByDescending(row => row.Version).First());
        var bypassCandidateIds = testReports.Select(x => x.Id)
            .Concat(applicationFiles.Select(x => x.VerificationApplicationId)).Distinct().ToArray();
        var bypassedIds = bypassCandidateIds.Length == 0
            ? []
            : (await db.ApprovalAuditEvents.AsNoTracking()
                .Where(x => bypassCandidateIds.Contains(x.SubmissionId) &&
                            x.Action == ApprovalAuditActions.ApprovalBypassed)
                .Select(x => x.SubmissionId).ToListAsync(cancellationToken)).ToHashSet();
        var approvalCandidateIds = equipmentFiles.Select(x => x.Id).Concat(vendorFiles.Select(x => x.Id)).ToArray();
        var supervisorApprovedIds = approvalCandidateIds.Length == 0
            ? []
            : (await db.PendingSubmissions.AsNoTracking()
                .Where(x => approvalCandidateIds.Contains(x.Id) && x.Status == ApprovalStatus.Committed &&
                            x.ReviewerAccount != null && x.ReviewedAt != null)
                .Select(x => x.Id).ToListAsync(cancellationToken)).ToHashSet();

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
                    .Select(x => ApplyStatuses(new SystemDocumentFileDto
                    {
                        SourceKey = $"test-report:{x.Id:N}", FileName = x.FileName,
                        SourceModule = "測試報告", UploadedBy = x.UploadEmp, ContentType = x.ContentType,
                        IsImage = IsImage(x.ContentType, x.FileName), FileSize = x.FileSize,
                        UploadedAt = x.CreatedAt, ProcessingStatusDisplay = "正式入庫完成",
                        ProcessingStatusCssClass = "completed", DownloadUrl = $"/api/files/{x.Id}/download",
                        PreviewUrl = $"/api/files/{x.Id}/download", CanDelete = false,
                        DeletionRestrictionMessage = "測試報告必須由原測試案件生命週期管理。",
                        SourceManagementUrl = TestReportSourceUrl(x, reportModuleCodes)
                    }, bypassedIds.Contains(x.Id) ? AttachmentStatusPresentation.Exempt() : AttachmentStatusPresentation.Approved(),
                        AttachmentStatusPresentation.Archive("Completed"),
                        SourceKm(sourceKm, KmIntegrationSourceEntityTypes.TestReport, x.Id))).ToList();
                folders.Add(SystemFolder($"{modelKey}:reports", modelKey, 3, "測試報告",
                    "已核准、已正式完成的測試報告。", 10, reportFiles));

                var productFiles = productInformationFiles.Where(x => string.Equals(
                        x.Folder.Parent!.Name, modelName, StringComparison.OrdinalIgnoreCase))
                    .Select(x => ToSystemFile(x,
                        productDeletionJobs.GetValueOrDefault(x.Id))).ToList();
                folders.Add(SystemFolder($"{modelKey}:product", modelKey, 3, "產品資訊",
                    "文件專區直接上傳的產品資訊。", 20, productFiles, canUpload: true,
                    uploadModelName: modelName));

                var linkedFiles = applicationFiles.Where(x => string.Equals(
                        x.VerificationApplication.ProductModel, modelName, StringComparison.OrdinalIgnoreCase))
                    .Select(x => ApplyStatuses(new SystemDocumentFileDto
                    {
                        SourceKey = $"application-document:{x.DocumentLibraryFileId!.Value:N}", FileName = x.FileName,
                        SourceModule = "驗證申請", UploadedBy = x.UploadedBy, ContentType = x.ContentType,
                        IsImage = IsImage(x.ContentType, x.FileName), FileSize = x.FileSize,
                        UploadedAt = x.CreatedAt, ProcessingStatusDisplay = "正式入庫完成",
                        ProcessingStatusCssClass = "completed",
                        DownloadUrl = $"/api/document-library/files/{x.DocumentLibraryFileId}/download",
                        PreviewUrl = $"/api/document-library/files/{x.DocumentLibraryFileId}/preview",
                        CanDelete = false,
                        DeletionRestrictionMessage = "驗證申請附件必須由原申請生命週期管理。",
                        SourceManagementUrl = $"/verification-applications/{x.VerificationApplicationId}"
                    }, bypassedIds.Contains(x.VerificationApplicationId) ? AttachmentStatusPresentation.Exempt() : AttachmentStatusPresentation.Approved(),
                        AttachmentStatusPresentation.Archive("Completed"),
                        SourceKm(sourceKm, KmIntegrationSourceEntityTypes.VerificationApplicationFile, x.Id))).ToList();
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
                var files = group.Select(x => ApplyStatuses(new SystemDocumentFileDto
                {
                    SourceKey = $"equipment:{x.Id:N}", FileName = x.OriginalFileName ?? x.FileName,
                    SourceModule = "設備管理", UploadedBy = x.UploadEmp, ContentType = x.ContentType,
                    IsImage = IsImage(x.ContentType, x.OriginalFileName ?? x.FileName), FileSize = x.FileSize,
                    UploadedAt = x.CreatedAt, ProcessingStatusDisplay = "正式入庫完成",
                    ProcessingStatusCssClass = "completed", DownloadUrl = $"/api/assets/files/{x.Id}/content",
                    PreviewUrl = $"/api/assets/files/{x.Id}/image", CanDelete = false,
                    DeletionRestrictionMessage = "設備附件必須由設備管理的安全刪除流程處理。",
                    SourceManagementUrl = $"/assets/equipment/{x.ApparatusId}/edit"
                }, supervisorApprovedIds.Contains(x.Id) ? AttachmentStatusPresentation.Approved() : AttachmentStatusPresentation.Exempt(),
                    AttachmentStatusPresentation.Archive(x.ArchiveStatus),
                    SourceKm(sourceKm, KmIntegrationSourceEntityTypes.ApparatusFile, x.Id,
                        IsImage(x.ContentType, x.OriginalFileName ?? x.FileName), configuredKm.EquipmentEnabled))).ToList();
                folders.Add(SystemFolder(SystemKey("equipment", group.Key), equipmentKey, 3, name,
                    $"設備 ID：{group.Key}", 10, files));
            }
            foreach (var group in vendorFiles.GroupBy(x => x.VendorId).OrderBy(x => x.First().Vendor.Name))
            {
                var vendor = group.First().Vendor;
                var files = group.Select(x => ApplyStatuses(new SystemDocumentFileDto
                {
                    SourceKey = $"vendor:{x.Id:N}", FileName = x.OriginalFileName,
                    SourceModule = "廠商管理", UploadedBy = x.UploadedBy, ContentType = x.ContentType,
                    IsImage = IsImage(x.ContentType, x.OriginalFileName), FileSize = x.FileSize,
                    UploadedAt = x.UploadedAt, ProcessingStatusDisplay = "正式入庫完成",
                    ProcessingStatusCssClass = "completed", DownloadUrl = $"/api/vendors/files/{x.Id}/content",
                    PreviewUrl = $"/api/vendors/files/{x.Id}/content", CanDelete = false,
                    DeletionRestrictionMessage = "廠商附件必須由廠商管理的原始生命週期處理。",
                    SourceManagementUrl = $"/vendors/{x.VendorId}/edit"
                }, supervisorApprovedIds.Contains(x.Id) ? AttachmentStatusPresentation.Approved() : AttachmentStatusPresentation.Exempt(),
                    AttachmentStatusPresentation.Archive(x.ArchiveStatus),
                    SourceKm(sourceKm, KmIntegrationSourceEntityTypes.VendorFile, x.Id,
                        moduleEnabled: configuredKm.VendorEnabled))).ToList();
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

    public async Task<List<DocumentArchiveStatusDto>> GetArchiveStatusesAsync(
        IReadOnlyCollection<Guid> fileIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        var ids = fileIds.Where(x => x != Guid.Empty).Distinct().ToArray();
        if (ids.Length > MaximumArchiveStatusBatchSize)
            throw new ArgumentException($"一次最多查詢 {MaximumArchiveStatusBatchSize} 份文件狀態。", nameof(fileIds));
        if (ids.Length == 0) return [];

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.DocumentLibraryFiles.AsNoTracking()
            .Where(x => ids.Contains(x.Id))
            .Select(x => new
            {
                x.Id,
                ArchiveStatus = x.ProcessingJob == null
                    ? DocumentArchiveStatuses.NotStarted
                    : x.ProcessingJob.ArchiveStatus,
                ArchiveError = x.ProcessingJob == null ? null : x.ProcessingJob.ArchiveLastError,
                ArchiveNextRetryAt = x.ProcessingJob == null ? null : x.ProcessingJob.ArchiveNextRetryAt
            })
            .ToListAsync(cancellationToken);

        return rows.Select(x =>
        {
            var presentation = DocumentArchivePresentation(x.ArchiveStatus);
            return new DocumentArchiveStatusDto
            {
                FileId = x.Id,
                ArchiveStatus = x.ArchiveStatus,
                ArchiveStatusDisplay = presentation.Text,
                ArchiveStatusTone = presentation.Tone,
                ArchiveError = x.ArchiveError,
                ContinueTracking = x.ArchiveStatus is DocumentArchiveStatuses.Pending or
                    DocumentArchiveStatuses.Archiving ||
                    x.ArchiveStatus == DocumentArchiveStatuses.Failed && x.ArchiveNextRetryAt.HasValue
            };
        }).ToList();
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
            .Include(x => x.Parent).ThenInclude(x => x!.Parent)
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
        var fileIds = files.Select(x => x.Id).ToArray();
        var deletionJobs = await db.DocumentLibraryFileDeletionJobs.AsNoTracking()
            .Where(x => fileIds.Contains(x.DocumentFileId))
            .ToDictionaryAsync(x => x.DocumentFileId, cancellationToken);
        var referencedIds = (await db.VerificationApplicationFiles.AsNoTracking()
            .Where(x => x.DocumentLibraryFileId.HasValue &&
                        fileIds.Contains(x.DocumentLibraryFileId.Value))
            .Select(x => x.DocumentLibraryFileId!.Value).ToListAsync(cancellationToken)).ToHashSet();
        return files.Select(x => ToDto(x, x.ProcessingJob,
            x.ProcessingJob is not null && kmJobs.TryGetValue(x.ProcessingJob.Id, out var kmJobId)
                ? kmJobId : null, deletionJobs.GetValueOrDefault(x.Id),
            !referencedIds.Contains(x.Id))).ToList();
    }

    public async Task<DocumentLibraryFileDto> UploadAsync(
        Guid folderId,
        IFormFile file,
        string uploadedBy,
        CancellationToken cancellationToken = default)
    {
        await businessWrite.DemandAsync(cancellationToken);
        return await StageApprovedAsync(Guid.NewGuid(), folderId, file, uploadedBy,
            cancellationToken: cancellationToken);
    }

    public async Task<DocumentLibraryFileDto> StageApprovedAsync(
        Guid documentFileId,
        Guid folderId,
        IFormFile file,
        string uploadedBy,
        DocumentLibraryBypassAudit? bypassAudit = null,
        DateTime? receivedAt = null,
        CancellationToken cancellationToken = default)
        => await StageApprovedCoreAsync(documentFileId, folderId, null, file, uploadedBy,
            bypassAudit, receivedAt, cancellationToken);

    public async Task<DocumentLibraryFileDto> StageApprovedVersionAsync(
        Guid documentFileId,
        Guid previousFileId,
        IFormFile file,
        string uploadedBy,
        DocumentLibraryBypassAudit? bypassAudit = null,
        DateTime? receivedAt = null,
        CancellationToken cancellationToken = default)
        => await StageApprovedCoreAsync(documentFileId, null, previousFileId, file, uploadedBy,
            bypassAudit, receivedAt, cancellationToken);

    private async Task<DocumentLibraryFileDto> StageApprovedCoreAsync(
        Guid documentFileId,
        Guid? requestedFolderId,
        Guid? previousFileId,
        IFormFile file,
        string uploadedBy,
        DocumentLibraryBypassAudit? bypassAudit,
        DateTime? receivedAt,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 3;
        var stableReceivedAt = receivedAt?.ToUniversalTime() ?? DateTime.UtcNow;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await StageApprovedCoreAttemptAsync(documentFileId, requestedFolderId,
                    previousFileId, file, uploadedBy, bypassAudit, stableReceivedAt, cancellationToken);
            }
            catch (Exception ex) when (attempt < maximumAttempts && IsSerializationFailure(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken);
            }
        }
    }

    private async Task<DocumentLibraryFileDto> StageApprovedCoreAttemptAsync(
        Guid documentFileId,
        Guid? requestedFolderId,
        Guid? previousFileId,
        IFormFile file,
        string uploadedBy,
        DocumentLibraryBypassAudit? bypassAudit,
        DateTime receivedAt,
        CancellationToken cancellationToken)
    {
        if (documentFileId == Guid.Empty) throw new InvalidOperationException("文件識別碼不可空白。");
        ArgumentNullException.ThrowIfNull(file);
        if (file.Length <= 0) throw new InvalidOperationException("不可上傳空白檔案。");
        if (file.Length > options.Value.MaxFileSizeBytes)
            throw new InvalidOperationException($"檔案大小不可超過 {options.Value.MaxFileSizeBytes / 1_000_000} MB。");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await DocumentLibraryFileDeletionHold.ExistsAsync(db, documentFileId, cancellationToken))
            throw new InvalidOperationException("This document ID is permanently held by its deletion audit.");
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,
                cancellationToken) : null;
        DocumentLibraryFile? previous = null;
        if (previousFileId.HasValue)
        {
            previous = await db.DocumentLibraryFiles.Include(x => x.ProcessingJob)
                .SingleOrDefaultAsync(x => x.Id == previousFileId.Value, cancellationToken)
                ?? throw new KeyNotFoundException("The previous document version was not found.");
            if (previous.StorageArea != DocumentStorageAreas.Library ||
                previous.ProcessingJob?.ArchiveStatus != DocumentArchiveStatuses.Completed ||
                await DocumentLibraryFileDeletionHold.ExistsAsync(db, previous.Id, cancellationToken))
                throw new InvalidOperationException(
                    "Only an available, formally archived document can be replaced.");
            var logicalId = previous.DocumentId == Guid.Empty ? previous.Id : previous.DocumentId;
            var latestId = await db.DocumentLibraryFiles.AsNoTracking()
                .Where(x => (x.DocumentId == logicalId || x.Id == logicalId))
                .OrderByDescending(x => x.Version).Select(x => x.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (latestId != previous.Id)
                throw new InvalidOperationException("Only the latest document version can be replaced.");
        }
        var folderId = previous?.FolderId ?? requestedFolderId
            ?? throw new InvalidOperationException("The document folder is missing.");
        var folder = await db.DocumentFolders.AsNoTracking()
            .Include(x => x.Parent).ThenInclude(x => x!.Parent)
            .SingleOrDefaultAsync(x => x.Id == folderId, cancellationToken)
            ?? throw new KeyNotFoundException("找不到資料夾。");
        if (folder.Level != DocumentLibraryRules.MaximumLevel)
            throw new InvalidOperationException("文件只能上傳至第三層資料夾。");
        if (await IsSystemManagedFolderAsync(db, folderId, cancellationToken) &&
            !await IsProductInformationFolderAsync(db, folderId, cancellationToken))
            throw new InvalidOperationException("系統來源文件為唯讀，請回原來源模組管理附件。");
        string expectedSha256;
        await using (var verificationStream = file.OpenReadStream())
            expectedSha256 = Convert.ToHexString(
                await SHA256.HashDataAsync(verificationStream, cancellationToken)).ToLowerInvariant();

        var existing = await db.DocumentLibraryFiles.AsNoTracking().Include(x => x.ProcessingJob)
            .SingleOrDefaultAsync(x => x.Id == documentFileId, cancellationToken);
        if (existing is not null)
        {
            var expectedName = DocumentLibraryRules.NormalizeOriginalFileName(file.FileName);
            var expectedDocumentId = previous is null
                ? documentFileId
                : previous.DocumentId == Guid.Empty ? previous.Id : previous.DocumentId;
            var expectedVersion = previous is null ? 1 : Math.Max(1, previous.Version) + 1;
            if (existing.DocumentId != expectedDocumentId ||
                existing.Version != expectedVersion ||
                existing.SupersedesFileId != previous?.Id ||
                existing.FolderId != folderId || existing.FileSize != file.Length ||
                !string.Equals(existing.OriginalFileName, expectedName, StringComparison.Ordinal) ||
                !string.Equals(existing.UploadedBy, uploadedBy.Trim(), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(existing.Sha256, expectedSha256, StringComparison.Ordinal))
                throw new InvalidOperationException("既有文件識別碼與本次核准內容不一致。");
            return ToDto(existing, existing.ProcessingJob, null);
        }

        var id = documentFileId;
        var now = receivedAt;
        var originalName = DocumentLibraryRules.NormalizeOriginalFileName(file.FileName);
        var standardName = DocumentLibraryRules.CreateStandardFileName(originalName, now, id);
        var relativeFolder = FormalBusinessPath.DocumentFolder(folder);
        var relativePath = Path.Combine(relativeFolder, standardName);
        var absolutePath = ResolvePath(DocumentStorageAreas.Staging, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);

        string sha256;
        var createdPhysicalFile = false;
        try
        {
            if (File.Exists(absolutePath))
            {
                var info = new FileInfo(absolutePath);
                await using var existingStream = new FileStream(absolutePath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 81920, useAsync: true);
                sha256 = Convert.ToHexString(
                    await SHA256.HashDataAsync(existingStream, cancellationToken)).ToLowerInvariant();
                if (info.Length != file.Length ||
                    !string.Equals(sha256, expectedSha256, StringComparison.Ordinal))
                    throw new InvalidOperationException("既有暫存檔案大小或雜湊與核准內容不一致。");
            }
            else
            {
                await using var source = file.OpenReadStream();
                await using var destination = new FileStream(
                    absolutePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                createdPhysicalFile = true;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    hash.AppendData(buffer, 0, read);
                }
                sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (!string.Equals(sha256, expectedSha256, StringComparison.Ordinal))
                    throw new InvalidOperationException("寫入後的暫存檔案雜湊與上傳內容不一致。");
            }

            var entity = new DocumentLibraryFile
            {
                Id = id,
                DocumentId = previous is null
                    ? id
                    : previous.DocumentId == Guid.Empty ? previous.Id : previous.DocumentId,
                Version = previous is null ? 1 : Math.Max(1, previous.Version) + 1,
                SupersedesFileId = previous?.Id,
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
                ArchiveStatus = DocumentArchiveStatuses.Pending,
                AttemptCount = 0,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.DocumentLibraryFiles.Add(entity);
            db.DocumentProcessingJobs.Add(job);
            if (bypassAudit is not null)
            {
                db.ApprovalAuditEvents.Add(ApprovalAuditEvent.Create(entity.Id,
                    ApprovalSourceMenuBinding.DocumentLibraryMenu,
                    ApprovalSourceMenuBinding.DocumentLibraryModule,
                    ApprovalAuditActions.ApprovalBypassed, bypassAudit.ActorAccount, now, null, null,
                    details: $"Basis={bypassAudit.Basis}"));
            }
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return ToDto(entity, job, null);
        }
        catch
        {
            if (createdPhysicalFile && File.Exists(absolutePath)) File.Delete(absolutePath);
            throw;
        }
    }

    private static bool IsSerializationFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure })
                return true;
        return false;
    }

    public async Task<DocumentLibraryFileDto> UploadProductInformationAsync(
        string modelName,
        IFormFile file,
        string uploadedBy,
        CancellationToken cancellationToken = default)
    {
        await businessWrite.DemandAsync(cancellationToken);
        var folderId = await GetOrCreateProductInformationFolderAsync(modelName, cancellationToken);
        return await StageApprovedAsync(Guid.NewGuid(), folderId, file, uploadedBy,
            cancellationToken: cancellationToken);
    }

    public async Task<Guid> GetOrCreateProductInformationFolderAsync(
        string modelName,
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
        return product.Id;
    }

    public async Task<DocumentDownload?> OpenDownloadAsync(
        Guid fileId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.DocumentLibraryFiles.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == fileId, cancellationToken);
        if (entity is null) return null;
        if (await DocumentLibraryFileDeletionHold.IsActiveAsync(db, fileId, cancellationToken))
            return null;
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

    private static string? TestReportSourceUrl(ModuleCaseFile file,
        IReadOnlyDictionary<Guid, string> moduleCodes)
    {
        if (!moduleCodes.TryGetValue(file.RecordId, out var moduleCode) ||
            string.IsNullOrWhiteSpace(moduleCode)) return null;
        return file.CaseId.HasValue && file.TaskId.HasValue
            ? $"/modules/{moduleCode}/{file.RecordId}/cases/{file.CaseId}/tasks/{file.TaskId}"
            : $"/modules/{moduleCode}/detail/{file.RecordId}";
    }

    private static string EquipmentFolderName(Apparatus equipment)
    {
        var assetNumber = string.IsNullOrWhiteSpace(equipment.ProductsId)
            ? "未設定財產編號"
            : equipment.ProductsId.Trim();
        return $"{assetNumber} - {equipment.Name}";
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

    private SystemDocumentFileDto ToSystemFile(DocumentLibraryFile file,
        DocumentLibraryFileDeletionJob? deletion = null)
    {
        var dto = ToDto(file, file.ProcessingJob, null);
        var download = $"/api/document-library/files/{file.Id}/download";
        return new SystemDocumentFileDto
        {
            FileId = file.Id,
            SourceKey = $"document-library:{file.Id:N}",
            FileName = file.OriginalFileName,
            SourceModule = "文件專區",
            UploadedBy = file.UploadedBy,
            ContentType = file.ContentType,
            IsImage = IsImage(file.ContentType, file.OriginalFileName),
            FileSize = file.FileSize,
            UploadedAt = file.UploadedAt,
            ProcessingStatusDisplay = dto.ProcessingStatusDisplay,
            ProcessingStatusCssClass = dto.ProcessingStatusCssClass,
            ReviewStatusDisplay = dto.ReviewStatusDisplay,
            ReviewStatusTone = dto.ReviewStatusTone,
            ArchiveStatusDisplay = dto.ArchiveStatusDisplay,
            ArchiveStatusTone = dto.ArchiveStatusTone,
            ArchiveStatus = dto.ArchiveStatus,
            ArchiveError = dto.ArchiveError,
            KmStatusDisplay = dto.KmStatusDisplay,
            KmStatusTone = dto.KmStatusTone,
            DownloadUrl = download,
            PreviewUrl = $"/api/document-library/files/{file.Id}/preview",
            CanDelete = deletion is null,
            DeletionRequestId = deletion?.Id,
            DeletionStatus = deletion?.Status,
            DeletionStatusDisplay = deletion is null
                ? null : DocumentLibraryFileDeletionService.StatusDisplay(deletion.Status),
            DeletionError = deletion?.LastErrorMessage
        };
    }

    private AttachmentStatusValue SourceKm(
        IReadOnlyDictionary<(string Type, Guid Id), KmIntegrationOutbox> rows,
        string sourceType,
        Guid sourceId,
        bool excludedByPolicy = false,
        bool moduleEnabled = true)
    {
        rows.TryGetValue((sourceType, sourceId), out var row);
        return AttachmentStatusPresentation.Km(row is not null, configuredKm.Enabled && moduleEnabled,
            row?.Status.ToString(), excludedByPolicy);
    }

    private static SystemDocumentFileDto ApplyStatuses(
        SystemDocumentFileDto file,
        AttachmentStatusValue review,
        AttachmentStatusValue archive,
        AttachmentStatusValue km)
    {
        file.ReviewStatusDisplay = review.Text;
        file.ReviewStatusTone = review.Tone;
        file.ArchiveStatusDisplay = archive.Text;
        file.ArchiveStatusTone = archive.Tone;
        file.KmStatusDisplay = km.Text;
        file.KmStatusTone = km.Tone;
        return file;
    }

    private static AttachmentStatusValue DocumentArchivePresentation(string? status) => status switch
    {
        DocumentArchiveStatuses.Pending => new("待正式入庫", "pending"),
        DocumentArchiveStatuses.Archiving => new("正式入庫中", "pending"),
        DocumentArchiveStatuses.Completed => new("正式入庫完成", "success"),
        DocumentArchiveStatuses.Failed => new("寫入失敗", "danger"),
        _ => new("尚未入庫", "neutral")
    };

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

    private DocumentLibraryFileDto ToDto(
        DocumentLibraryFile entity,
        DocumentProcessingJob? job,
        string? kmJobId,
        DocumentLibraryFileDeletionJob? deletion = null,
        bool sourceAllowsDeletion = true) => new()
    {
        Id = entity.Id,
        DocumentId = entity.DocumentId == Guid.Empty ? entity.Id : entity.DocumentId,
        Version = Math.Max(1, entity.Version),
        SupersedesFileId = entity.SupersedesFileId,
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
        ArchiveStatus = job?.ArchiveStatus ?? DocumentArchiveStatuses.NotStarted,
        ReviewStatusDisplay = "—",
        ReviewStatusTone = "muted",
        ArchiveStatusDisplay = DocumentArchivePresentation(job?.ArchiveStatus).Text,
        ArchiveStatusTone = DocumentArchivePresentation(job?.ArchiveStatus).Tone,
        KmStatusDisplay = AttachmentStatusPresentation.Km(
            job?.ArchiveStatus == DocumentArchiveStatuses.Completed, configuredKm.Enabled, job?.Status).Text,
        KmStatusTone = AttachmentStatusPresentation.Km(
            job?.ArchiveStatus == DocumentArchiveStatuses.Completed, configuredKm.Enabled, job?.Status).Tone,
        ProcessingError = job?.KmErrorMessage ?? job?.LastError,
        ArchiveError = job?.ArchiveLastError,
        MetadataAvailable = !string.IsNullOrWhiteSpace(job?.MetadataJson),
        CanDelete = sourceAllowsDeletion && deletion is null,
        DeletionRequestId = deletion?.Id,
        DeletionStatus = deletion?.Status,
        DeletionStatusDisplay = deletion is null
            ? null : DocumentLibraryFileDeletionService.StatusDisplay(deletion.Status),
        DeletionError = deletion?.LastErrorMessage,
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
            _ when job?.ArchiveStatus == DocumentArchiveStatuses.Pending => "待正式 NAS 入庫",
            _ when job?.ArchiveStatus == DocumentArchiveStatuses.Archiving => "NAS 入庫中",
            _ when job?.ArchiveStatus == DocumentArchiveStatuses.Failed => "NAS 寫入失敗",
            DocumentProcessingStatuses.Pending when !string.IsNullOrWhiteSpace(job.LastError) => "KM 派送失敗／待重試",
            DocumentProcessingStatuses.Pending when job.ArchiveStatus == DocumentArchiveStatuses.Completed =>
                configuredKm.Enabled ? "正式入庫完成／待 KM 處理" : "正式入庫完成／KM 尚未啟用",
            DocumentProcessingStatuses.Accepted => "KM 已接受／等待處理",
            DocumentProcessingStatuses.Processing => "KM 處理中",
            DocumentProcessingStatuses.Completed when job.ArchiveStatus == DocumentArchiveStatuses.Completed =>
                "正式入庫完成／KM 分析完成",
            DocumentProcessingStatuses.Completed => "AI 已完成",
            DocumentProcessingStatuses.Failed => "KM 處理失敗",
            _ => "既有文件"
        }
    };
}
