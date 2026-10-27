using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Models.Config;
using SIT.DepartmentSystem.Web.Services.Approval;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace SIT.DepartmentSystem.Web.Services;

public interface IVerificationCaseDeletionPreviewService
{
    Task<VerificationCaseDeletionPreviewDto> GetAsync(
        Guid recordId, ClaimsPrincipal user, CancellationToken cancellationToken = default);
}

/// <summary>
/// Read-only, exact-ID inventory. It never starts deletion or contacts KM.
/// </summary>
public sealed class VerificationCaseDeletionPreviewService(
    AppDbContext db,
    IBusinessWriteAuthorizationGuard businessWrite,
    IOptions<DocumentStorageOptions> documentStorage,
    IOptions<DocumentStagingOptions> documentStaging,
    IOptions<KmDeletionOptions>? deletionOptions = null,
    VerificationAttachmentArchiveProcessor? archive = null) : IVerificationCaseDeletionPreviewService
{
    public async Task<VerificationCaseDeletionPreviewDto> GetAsync(
        Guid recordId, ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        if (!user.HasClaim(SystemAuthorization.AccessScopeClaim, SystemAuthorization.AccessScopes.CsitStaff))
            throw new UnauthorizedAccessException("CSIT staff access is required.");
        await businessWrite.DemandAsync(cancellationToken);
        var application = await db.VerificationApplications.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Status == VerificationApplicationStatus.Accepted && x.ModuleRecordId == recordId,
            cancellationToken) ?? throw new KeyNotFoundException("The accepted verification case was not found.");
        VerificationApplicationSecurity.EnsureAcceptedManagerAccess(application, user);

        var record = await db.ModuleRecords.AsNoTracking().SingleOrDefaultAsync(x => x.Id == recordId,
            cancellationToken) ?? throw new InvalidOperationException("The approved ModuleRecord is missing.");
        var cases = await db.ModuleRecordCases.AsNoTracking().Where(x => x.RecordId == recordId)
            .OrderBy(x => x.SortOrder).ToListAsync(cancellationToken);
        var caseIds = cases.Select(x => x.Id).ToList();
        var tasks = await db.ModuleRecordTasks.AsNoTracking().Where(x => caseIds.Contains(x.CaseId))
            .OrderBy(x => x.TaskNo).ToListAsync(cancellationToken);
        var files = await db.ModuleCaseFiles.AsNoTracking().Where(x => x.RecordId == recordId)
            .OrderBy(x => x.DocumentId).ThenBy(x => x.VersionNo).ToListAsync(cancellationToken);
        var attachments = await db.VerificationApplicationFiles.AsNoTracking()
            .Where(x => x.VerificationApplicationId == application.Id).OrderBy(x => x.FileName)
            .ToListAsync(cancellationToken);
        var documentFileIds = attachments.Where(x => x.DocumentLibraryFileId.HasValue)
            .Select(x => x.DocumentLibraryFileId!.Value).ToList();
        var documents = await db.DocumentLibraryFiles.AsNoTracking()
            .Where(x => documentFileIds.Contains(x.Id)).ToListAsync(cancellationToken);
        var processingJobs = await db.DocumentProcessingJobs.AsNoTracking()
            .Where(x => documentFileIds.Contains(x.DocumentFileId)).ToListAsync(cancellationToken);

        var sourceFileIds = files.Select(x => x.Id).Concat(attachments.Select(x => x.Id)).ToList();
        var outbox = await db.KmIntegrationOutbox.AsNoTracking()
            .Where(x => sourceFileIds.Contains(x.SourceEntityId))
            .OrderBy(x => x.CreatedAt).ToListAsync(cancellationToken);
        var contextKeys = BuildApprovalContextKeys(recordId, cases, tasks, files);
        var contextIds = contextKeys.Select(x => x.ContextId).Distinct().ToList();
        var pendingCandidates = await db.PendingSubmissions.AsNoTracking().Where(x =>
            x.SourceModuleCode == "verification" && contextIds.Contains(x.ContextId))
            .ToListAsync(cancellationToken);
        var submissions = pendingCandidates.Where(x => contextKeys.Contains((x.ContextType, x.ContextId)))
            .OrderBy(x => x.SubmittedAt).ToList();
        var auditSubmissionIds = submissions.Select(x => x.Id).Append(application.Id).Distinct().ToList();
        var audit = await db.ApprovalAuditEvents.AsNoTracking()
            .Where(x => auditSubmissionIds.Contains(x.SubmissionId)).OrderBy(x => x.OccurredAt)
            .ToListAsync(cancellationToken);
        var documentIds = files.Where(x => x.DocumentId != Guid.Empty).Select(x => x.DocumentId)
            .Distinct().ToList();
        var sharedDocumentIds = await db.ModuleCaseFiles.AsNoTracking()
            .Where(x => x.RecordId != recordId && documentIds.Contains(x.DocumentId))
            .Select(x => x.DocumentId).Distinct().ToListAsync(cancellationToken);
        var approvalRoot = await db.SystemOptions.AsNoTracking()
            .Where(x => x.Category == "ApprovalStagingRoot" && x.Name == "default" && x.IsEnabled)
            .Select(x => x.Value).FirstOrDefaultAsync(cancellationToken);

        var items = new List<VerificationCaseDeletionInventoryItemDto>
        {
            new("VerificationApplication", application.Id, null, null, null,
                application.ApplicationNo, application.Status.ToString()),
            new("ModuleRecord", record.Id, application.Id, null, null,
                record.RecordNo, record.Status)
        };
        items.AddRange(cases.Select(x => new VerificationCaseDeletionInventoryItemDto(
            "Case", x.Id, recordId, null, null, x.Name, x.Status)));
        items.AddRange(tasks.Select(x => new VerificationCaseDeletionInventoryItemDto(
            "Task", x.Id, x.CaseId, null, null, x.Name, x.Status)));

        foreach (var attachment in attachments)
        {
            items.Add(new("VerificationApplicationAttachment", attachment.Id, application.Id,
                attachment.DocumentLibraryFileId, 1, attachment.FileName,
                attachment.DocumentLibraryFileId.HasValue ? "Archived" : "Staging"));
            items.Add(new("PhysicalStaging", attachment.Id, application.Id, null, 1,
                attachment.FileName, null, DocumentStorageAreas.Staging,
                RelativeFileExists(documentStaging.Value.RootPath, attachment.FilePath)));
            if (attachment.DocumentLibraryFileId is Guid documentId)
            {
                var document = documents.SingleOrDefault(x => x.Id == documentId);
                items.Add(new("DocumentLibraryFile", documentId, attachment.Id, documentId, 1,
                    document?.OriginalFileName ?? attachment.FileName,
                    document is null ? "Missing" : "Linked", document?.StorageArea));
                if (document is not null)
                    items.Add(new("PhysicalDocument", document.Id, attachment.Id, document.Id, 1,
                        document.StandardFileName, null, document.StorageArea,
                        RelativeFileExists(DocumentRoot(document.StorageArea), document.RelativePath)));
            }
        }
        items.AddRange(processingJobs.Select(x => new VerificationCaseDeletionInventoryItemDto(
            "DocumentProcessingJob", x.Id, x.DocumentFileId, x.DocumentFileId,
            null, x.Id.ToString("N"), x.Status)));

        foreach (var file in files)
        {
            items.Add(new(file.FileKind == ModuleCaseFileKinds.TestReport ? "TestReport" : "ModuleCaseAttachment",
                file.Id, file.TaskId ?? file.CaseId ?? recordId,
                file.DocumentId == Guid.Empty ? null : file.DocumentId,
                file.VersionNo > 0 ? file.VersionNo : null, file.FileName,
                file.ReviewStatus ?? (file.IsFinal ? "Final" : null)));
            items.Add(new("PhysicalCaseFile", file.Id, recordId,
                file.DocumentId == Guid.Empty ? null : file.DocumentId,
                file.VersionNo > 0 ? file.VersionNo : null, file.FileName, null,
                "CaseFile", AbsoluteFileExists(file.FilePath, file.FileName)));
            if (!string.IsNullOrWhiteSpace(file.NasFilePath))
                items.Add(new("PhysicalNasMirror", file.Id, recordId,
                    file.DocumentId == Guid.Empty ? null : file.DocumentId,
                    file.VersionNo > 0 ? file.VersionNo : null, file.FileName, null,
                    "NAS", AbsoluteFileExists(file.NasFilePath)));
            if (!string.IsNullOrWhiteSpace(file.RawJsonPath))
                items.Add(new("SharedRawDataIndex", file.Id, recordId, null, null,
                    "rawdata.json", "Shared; never delete by Model Name", "NAS",
                    AbsoluteFileExists(file.RawJsonPath)));
        }

        items.AddRange(submissions.Select(x => new VerificationCaseDeletionInventoryItemDto(
            "PendingSubmission", x.Id, null, null, null,
            x.OriginalFileName ?? x.ContextDisplay ?? x.SubmissionKey, x.Status.ToString())));
        items.AddRange(submissions.Where(x => x.StagingStorageKey is not null).Select(x =>
            new VerificationCaseDeletionInventoryItemDto("PhysicalApprovalStaging", x.Id, null,
                null, null, x.OriginalFileName ?? x.SubmissionKey, x.Status.ToString(),
                "ApprovalStaging", RelativeFileExists(approvalRoot, x.StagingStorageKey!))));
        items.AddRange(outbox.Select(x => new VerificationCaseDeletionInventoryItemDto(
            "KmOutbox", x.Id, x.SourceEntityId, x.DocumentId, x.Version,
            x.EventType, x.Status.ToString(), KmJobId: x.KmJobId)));
        items.AddRange(audit.Select(x => new VerificationCaseDeletionInventoryItemDto(
            "ApprovalAudit", x.Id, x.SubmissionId, null, null,
            x.Action, x.OccurredAt.ToString("O"))));

        var enabled = deletionOptions?.Value.Enabled == true;
        var blockers = new List<string>();
        if (!enabled)
        {
            blockers.Add("v1.2 附錄 B 已確認；KM 刪除 API 與資料庫 Migration 尚未驗收，目前僅提供盤點。");
            if (files.Any(x => x.FileKind == ModuleCaseFileKinds.TestReport &&
                (x.ReviewStatus == TestReportReviewStatuses.Approved || x.IsFinal)))
                blockers.Add("包含已核准或正式 TestReport，須先確認留存及 KM 清理契約。");
            if (attachments.Any(x => x.DocumentLibraryFileId.HasValue))
                blockers.Add("包含已正式歸檔的申請附件，不能直接移除 P1 文件紀錄。");
            if (outbox.Count > 0)
                blockers.Add("存在 KM 派送或索引紀錄；HTTP 202 與既有 ingestion 狀態均不能代表刪除完成。");
        }
        if (submissions.Any(x => x.Status is ApprovalStatus.Pending or ApprovalStatus.ApprovedPendingCommit
            or ApprovalStatus.CommitFailed))
            blockers.Add("存在待審或待 Commit 的上傳，刪除前須停止並處理競爭。");
        if (outbox.Any(x => x.Status == KmIntegrationStatus.Dispatching))
            blockers.Add("KM 正在派送或派送結果不明，須先等待或確認。");
        if (processingJobs.Any(x => x.Status is DocumentProcessingStatuses.Pending or
            DocumentProcessingStatuses.Processing))
            blockers.Add("文件處理工作仍在執行。");
        if (archive is not null && (await archive.ReadPendingAsync(cancellationToken))
            .Any(x => x.ApplicationId == application.Id))
            blockers.Add("申請附件仍在歸檔中。");
        if (sharedDocumentIds.Count > 0)
            blockers.Add("文件版本 ID 同時出現在其他案件；不得刪除共享資料。");
        if (items.Any(x => x.PhysicalExists is null && x.Kind.StartsWith("Physical", StringComparison.Ordinal)))
            blockers.Add("部分實體檔案路徑無法安全解析，須先人工確認。");
        var deletionJob = await db.VerificationCaseDeletionJobs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RecordId == recordId, cancellationToken);
        if (deletionJob is not null && deletionJob.Status != "Prepared")
            blockers.Add("案件已進入刪除流程，不能再次確認。");

        return new VerificationCaseDeletionPreviewDto
        {
            ApplicationId = application.Id, RecordId = recordId,
            ApplicationNo = application.ApplicationNo,
            ModelName = record.CustomerProductName ?? application.ProductModel ?? string.Empty,
            Items = items, BlockingReasons = blockers,
            ExecutionEnabled = enabled,
            CanExecute = enabled && blockers.Count == 0,
            DeletionStatus = deletionJob?.Status
        };
    }

    private string? DocumentRoot(string area) => area switch
    {
        DocumentStorageAreas.Library => documentStorage.Value.RootPath,
        DocumentStorageAreas.Staging => documentStaging.Value.RootPath,
        _ => null
    };

    internal static HashSet<(string ContextType, string ContextId)> BuildApprovalContextKeys(
        Guid recordId, IReadOnlyList<ModuleRecordCase> cases, IReadOnlyList<ModuleRecordTask> tasks,
        IReadOnlyList<ModuleCaseFile> files)
    {
        var keys = new HashSet<(string, string)>();
        void Add(ThreeLevelUploadTarget target) => keys.Add((target.ContextType, target.ContextId));
        foreach (var recordCase in cases)
        {
            Add(new(ThreeLevelUploadKind.CaseAttachment, recordCase.Id));
            Add(new(ThreeLevelUploadKind.CaseAttachmentByNo, recordId, recordCase.CaseNo));
        }
        foreach (var task in tasks)
        {
            Add(new(ThreeLevelUploadKind.TaskAttachment, task.Id));
            Add(new(ThreeLevelUploadKind.TestReport, task.Id));
            Add(new(ThreeLevelUploadKind.TaskAttachmentByNo, task.CaseId, task.TaskNo));
            Add(new(ThreeLevelUploadKind.TestReportByTaskNo, task.CaseId, task.TaskNo));
        }
        foreach (var file in files.Where(x => x.FileKind == ModuleCaseFileKinds.TestReport))
            Add(new(ThreeLevelUploadKind.TestReportVersion, file.Id));
        return keys;
    }

    private static bool? RelativeFileExists(string? root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathFullyQualified(relativePath)) return null;
        try
        {
            var basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullPath = Path.GetFullPath(Path.Combine(basePath, relativePath));
            if (!fullPath.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return null;
            return File.Exists(fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool? AbsoluteFileExists(string? path, string? name = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            if (name is not null && (Path.IsPathFullyQualified(name) || name != Path.GetFileName(name)))
                return null;
            var fullPath = name is null ? path : Path.Combine(path, name);
            return Path.IsPathFullyQualified(fullPath) ? File.Exists(fullPath) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
