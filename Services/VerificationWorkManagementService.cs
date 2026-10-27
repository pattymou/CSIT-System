using System.Text.Json;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services.Interfaces;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace SIT.DepartmentSystem.Web.Services;

public interface IVerificationWorkManagementService
{
    Task<IReadOnlyList<VerificationWorkListItemDto>> ListAsync(CancellationToken cancellationToken = default);
    Task<VerificationWorkDetailDto?> GetAsync(Guid recordId, CancellationToken cancellationToken = default);
    Task<VerificationWorkTaskDetailDto?> GetTaskAsync(Guid recordId, Guid taskId, CancellationToken cancellationToken = default);
    Task UpdateRecordStatusAsync(Guid recordId, UpdateVerificationWorkStatusRequest request, ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task UpdateTaskAsync(Guid taskId, UpdateVerificationWorkTaskRequest request, CancellationToken cancellationToken = default);
    Task UpdateTaskForRecordAsync(Guid recordId, Guid taskId, UpdateVerificationWorkTaskRequest request, CancellationToken cancellationToken = default);
}

public sealed class VerificationWorkManagementService(
    AppDbContext db,
    IAssignableEngineerDirectory engineerDirectory,
    IBusinessWriteAuthorizationGuard businessWrite,
    IOptions<KmIntegrationOptions>? kmOptions = null) : IVerificationWorkManagementService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> Statuses = new(StringComparer.OrdinalIgnoreCase)
        { "Open", "Close", "Hold" };

    public async Task<IReadOnlyList<VerificationWorkListItemDto>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await db.VerificationApplications.AsNoTracking()
            .Where(x => x.Status == VerificationApplicationStatus.Accepted && x.ModuleRecordId != null)
            .Include(x => x.ModuleRecord!).ThenInclude(x => x.Cases).ThenInclude(x => x.Tasks)
            .OrderByDescending(x => x.AcceptedAt)
            .ToListAsync(cancellationToken);
        return rows.Select(ToListItem).ToList();
    }

    public async Task<VerificationWorkDetailDto?> GetAsync(
        Guid recordId,
        CancellationToken cancellationToken = default)
    {
        var application = await db.VerificationApplications.AsNoTracking()
            .Where(x => x.Status == VerificationApplicationStatus.Accepted && x.ModuleRecordId == recordId)
            .Include(x => x.ModuleRecord!).ThenInclude(x => x.Cases).ThenInclude(x => x.Tasks)
            .SingleOrDefaultAsync(cancellationToken);
        if (application?.ModuleRecord is null) return null;

        var tasks = application.ModuleRecord.Cases.SelectMany(x => x.Tasks).ToList();
        var taskIds = tasks.Select(x => x.Id).ToList();
        var reports = await db.ModuleCaseFiles.AsNoTracking()
            .Where(x => x.TaskId != null && taskIds.Contains(x.TaskId.Value) &&
                        x.FileKind == ModuleCaseFileKinds.TestReport)
            .OrderByDescending(x => x.VersionNo)
            .ToListAsync(cancellationToken);
        var reportIds = reports.Select(x => x.Id).ToList();
        var pendingByReport = await db.PendingSubmissions.AsNoTracking()
            .Where(x => reportIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Status, cancellationToken);
        var kmByReport = await db.KmIntegrationOutbox.AsNoTracking()
            .Where(x => reportIds.Contains(x.SourceEntityId) &&
                        x.EventType == KmIntegrationEventTypes.TestReportCommitted &&
                        x.SourceEntityType == KmIntegrationSourceEntityTypes.TestReport)
            .ToDictionaryAsync(x => x.SourceEntityId, cancellationToken);
        var reportsByTask = reports.GroupBy(x => x.TaskId!.Value).ToDictionary(x => x.Key, x => x.ToList());
        var deletionStatus = await db.VerificationCaseDeletionJobs.AsNoTracking()
            .Where(x => x.RecordId == recordId).Select(x => x.Status)
            .SingleOrDefaultAsync(cancellationToken);
        var list = ToListItem(application);
        var (selectedItems, snapshotMessage) = SelectedItems(application, tasks);
        return new VerificationWorkDetailDto
        {
            ApplicationId = list.ApplicationId,
            DeletionStatus = deletionStatus,
            RecordId = list.RecordId,
            ApplicationNo = list.ApplicationNo,
            ModelName = list.ModelName,
            ProjectName = list.ProjectName,
            ApplicantName = list.ApplicantName,
            ProductType = list.ProductType,
            ExpectedEndDate = list.ExpectedEndDate,
            Status = list.Status,
            TotalTasks = list.TotalTasks,
            CompletedTasks = list.CompletedTasks,
            Progress = list.Progress,
            RequiresSetupReview = list.RequiresSetupReview,
            SelectedTestItems = selectedItems,
            SnapshotMessage = snapshotMessage,
            Tasks = application.ModuleRecord.Cases.OrderBy(x => x.SortOrder)
                .SelectMany(recordCase => recordCase.Tasks.OrderBy(x => x.CreatedAt)
                    .Select(task => new VerificationWorkTaskDto
                    {
                        Id = task.Id,
                        CaseId = task.CaseId,
                        Name = task.Name,
                        Category = recordCase.Name,
                        AssignEngineer = task.AssignEngineer,
                        Status = task.Status,
                        ActualCompletionDate = task.ActualCompletionDate,
                        Note = task.Note,
                        SourceTestItemCode = task.SourceTestItemCode,
                        Reports = reportsByTask.GetValueOrDefault(task.Id, [])
                            .Select(file => new VerificationWorkReportDto
                            {
                                Id = file.Id,
                                DocumentId = file.DocumentId,
                                FileName = file.FileName,
                                VersionNo = file.VersionNo,
                                ReviewStatus = file.ReviewStatus,
                                IsFinal = file.IsFinal,
                                FormalArchiveStatus = FormalArchiveStatus(file, pendingByReport),
                                FormalArchiveStatusDisplay = FormalArchiveText(file, pendingByReport),
                                KmAnalysisStatus = KmStatus(file, pendingByReport, kmByReport),
                                KmAnalysisStatusDisplay = KmText(file, pendingByReport, kmByReport,
                                    kmOptions?.Value.Enabled == true),
                                KmAnalysisError = kmByReport.TryGetValue(file.Id, out var km) &&
                                    km.Status == KmIntegrationStatus.Failed
                                        ? SafeKmError(km.LastErrorCode, km.LastErrorMessage) : null,
                                CreatedAt = file.CreatedAt
                            }).ToList()
                    })).ToList()
        };
    }

    private static bool IsFormal(ModuleCaseFile file,
        IReadOnlyDictionary<Guid, ApprovalStatus> pendingByReport) =>
        file.ReviewStatus == TestReportReviewStatuses.Approved && file.IsRawDataExported &&
        (!pendingByReport.TryGetValue(file.Id, out var approval) || approval == ApprovalStatus.Committed);

    private static string FormalArchiveStatus(ModuleCaseFile file,
        IReadOnlyDictionary<Guid, ApprovalStatus> pendingByReport)
    {
        if (pendingByReport.TryGetValue(file.Id, out var approval))
        {
            if (approval is ApprovalStatus.Pending or ApprovalStatus.ApprovedPendingCommit)
                return approval == ApprovalStatus.Pending ? "PendingApproval" : "Processing";
            if (approval == ApprovalStatus.CommitFailed) return "Failed";
            if (approval is ApprovalStatus.Rejected or ApprovalStatus.Withdrawn) return "NotCommitted";
        }
        if (IsFormal(file, pendingByReport)) return "Completed";
        if (file.ReviewStatus == TestReportReviewStatuses.Pending) return "PendingApproval";
        if (file.ReviewStatus == TestReportReviewStatuses.Approved) return "Processing";
        return "NotCommitted";
    }

    private static string FormalArchiveText(ModuleCaseFile file,
        IReadOnlyDictionary<Guid, ApprovalStatus> pendingByReport) =>
        FormalArchiveStatus(file, pendingByReport) switch
        {
            "PendingApproval" => "待主管審核",
            "Processing" => "正式入庫處理中",
            "Completed" => "正式入庫完成",
            "Failed" => "正式入庫失敗",
            _ => "尚未正式入庫"
        };

    private static string KmStatus(ModuleCaseFile file,
        IReadOnlyDictionary<Guid, ApprovalStatus> pendingByReport,
        IReadOnlyDictionary<Guid, KmIntegrationOutbox> kmByReport)
    {
        if (!IsFormal(file, pendingByReport)) return "NotSent";
        return kmByReport.TryGetValue(file.Id, out var km) ? km.Status.ToString() : "MissingOutbox";
    }

    private static string KmText(ModuleCaseFile file,
        IReadOnlyDictionary<Guid, ApprovalStatus> pendingByReport,
        IReadOnlyDictionary<Guid, KmIntegrationOutbox> kmByReport,
        bool kmEnabled)
    {
        var status = KmStatus(file, pendingByReport, kmByReport);
        return status switch
        {
            "Pending" => kmEnabled ? "待同步" : "待同步（KM 未啟用）",
            "Dispatching" => "同步中",
            "Sent" => "KM 已接收",
            "Processing" => "處理中",
            "Completed" => "完成",
            "Failed" => "失敗",
            "MissingOutbox" => "尚未建立同步工作",
            _ => "尚未送 KM"
        };
    }

    private static string? SafeKmError(string? code, string? message)
    {
        var safeMessage = KmIntegrationManualRetryAudit.SafeErrorMessage(message);
        var values = new[] { code, safeMessage }.Where(x => !string.IsNullOrWhiteSpace(x));
        var result = string.Join("：", values);
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    public async Task<VerificationWorkTaskDetailDto?> GetTaskAsync(
        Guid recordId, Guid taskId, CancellationToken cancellationToken = default)
    {
        var detail = await GetAsync(recordId, cancellationToken);
        var selected = detail?.SelectedTestItems.SingleOrDefault(x => x.TaskId == taskId);
        var task = detail?.Tasks.SingleOrDefault(x => x.Id == taskId);
        if (selected is null || task is null) return null;
        task.Name = selected.Name;
        task.Category = selected.Category;
        return new VerificationWorkTaskDetailDto
        {
            DeletionStatus = detail!.DeletionStatus,
            RecordId = recordId,
            ModelName = detail!.ModelName,
            ApplicationNo = detail.ApplicationNo,
            SelectedItem = selected,
            Task = task
        };
    }

    public async Task UpdateTaskForRecordAsync(
        Guid recordId, Guid taskId, UpdateVerificationWorkTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        var selected = await GetTaskAsync(recordId, taskId, cancellationToken);
        if (selected is null) throw new KeyNotFoundException("The selected test item does not belong to this verification case.");
        await UpdateTaskAsync(taskId, request, cancellationToken);
    }

    public async Task UpdateRecordStatusAsync(
        Guid recordId,
        UpdateVerificationWorkStatusRequest request,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        await businessWrite.DemandAsync(cancellationToken);
        await VerificationCaseDeletionHold.DemandWritableAsync(db, recordId, cancellationToken);
        var status = NormalizeStatus(request.Status);
        if (!user.HasClaim(SystemAuthorization.AccessScopeClaim, SystemAuthorization.AccessScopes.CsitStaff))
            throw new UnauthorizedAccessException("CSIT staff access is required.");
        var application = await db.VerificationApplications.SingleOrDefaultAsync(x =>
            x.Status == VerificationApplicationStatus.Accepted && x.ModuleRecordId == recordId,
            cancellationToken) ?? throw new KeyNotFoundException("The accepted verification case was not found.");
        VerificationApplicationSecurity.EnsureAcceptedManagerAccess(application, user);
        var record = await GetAcceptedRecordAsync(recordId, cancellationToken);
        if (string.Equals(record.Status, status, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The verification case already has this status.");
        var tasks = record.Cases.SelectMany(x => x.Tasks).ToList();
        if (status == "Close" && (tasks.Count == 0 || tasks.Any(x => !IsStatus(x.Status, "Close"))))
            throw new InvalidOperationException("All test items must be Close before the verification case can be closed.");
        record.Status = status;
        record.Progress = Progress(tasks);
        record.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateTaskAsync(
        Guid taskId,
        UpdateVerificationWorkTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        await businessWrite.DemandAsync(cancellationToken);
        var status = NormalizeStatus(request.Status);
        var task = await db.ModuleRecordTasks
            .Include(x => x.Case).ThenInclude(x => x.Record).ThenInclude(x => x.Cases).ThenInclude(x => x.Tasks)
            .SingleOrDefaultAsync(x => x.Id == taskId, cancellationToken)
            ?? throw new KeyNotFoundException("The verification test item was not found.");
        await VerificationCaseDeletionHold.DemandWritableAsync(db, task.Case.RecordId, cancellationToken);
        var accepted = await db.VerificationApplications.AnyAsync(x =>
            x.Status == VerificationApplicationStatus.Accepted && x.ModuleRecordId == task.Case.RecordId,
            cancellationToken);
        if (!accepted)
            throw new InvalidOperationException("The test item is not linked to an accepted verification application.");
        if (string.IsNullOrWhiteSpace(task.SourceTestItemCode))
            throw new InvalidOperationException("Historical test items without an application trace cannot be changed here.");
        if (status == "Close" && !request.ActualCompletionDate.HasValue)
            throw new InvalidOperationException("Actual completion date is required when a test item is closed.");

        task.AssignEngineer = await engineerDirectory.ResolveAssignableAccountAsync(
            task.Case.RecordId, request.AssignEngineer, cancellationToken);
        task.Status = status;
        task.ActualCompletionDate = request.ActualCompletionDate;
        task.Note = Clean(request.Note);
        task.Progress = status == "Close" ? 100 : 0;
        task.UpdatedAt = DateTime.UtcNow;

        var record = task.Case.Record;
        var allTasks = record.Cases.SelectMany(x => x.Tasks).ToList();
        foreach (var recordCase in record.Cases)
        {
            var caseTasks = recordCase.Tasks.ToList();
            recordCase.Status = caseTasks.Count > 0 && caseTasks.All(x => IsStatus(x.Status, "Close"))
                ? "Close"
                : caseTasks.Any(x => IsStatus(x.Status, "Hold")) ? "Hold" : "Open";
            recordCase.UpdatedAt = DateTime.UtcNow;
        }
        record.Progress = Progress(allTasks);
        if (IsStatus(record.Status, "Close") && allTasks.Any(x => !IsStatus(x.Status, "Close")))
            record.Status = "Open";
        record.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<ModuleRecord> GetAcceptedRecordAsync(Guid recordId, CancellationToken cancellationToken)
    {
        var accepted = await db.VerificationApplications.AnyAsync(x =>
            x.Status == VerificationApplicationStatus.Accepted && x.ModuleRecordId == recordId, cancellationToken);
        if (!accepted) throw new KeyNotFoundException("The accepted verification case was not found.");
        return await db.ModuleRecords.Include(x => x.Cases).ThenInclude(x => x.Tasks)
            .SingleAsync(x => x.Id == recordId, cancellationToken);
    }

    private static VerificationWorkListItemDto ToListItem(VerificationApplication application)
    {
        var record = application.ModuleRecord!;
        var tasks = record.Cases.SelectMany(x => x.Tasks).ToList();
        var completed = tasks.Count(x => IsStatus(x.Status, "Close"));
        return new VerificationWorkListItemDto
        {
            ApplicationId = application.Id,
            RecordId = record.Id,
            ApplicationNo = application.ApplicationNo,
            ModelName = record.CustomerProductName ?? application.ProductModel ?? string.Empty,
            ProjectName = record.Name,
            ApplicantName = application.ApplicantName,
            ProductType = ProductType(application),
            ExpectedEndDate = record.ExpectedEndDate,
            Status = record.Status,
            TotalTasks = tasks.Count,
            CompletedTasks = completed,
            Progress = Progress(tasks),
            RequiresSetupReview = tasks.Count == 0 || tasks.Any(x => string.IsNullOrWhiteSpace(x.SourceTestItemCode))
        };
    }

    private static string ProductType(VerificationApplication application)
    {
        if (!string.IsNullOrWhiteSpace(application.ValidationFormPayloadJson))
        {
            try
            {
                var request = JsonSerializer.Deserialize<ValidationRequestSubmissionDto>(
                    application.ValidationFormPayloadJson, JsonOptions);
                if (!string.IsNullOrWhiteSpace(request?.FormSnapshot?.ProductTypeLabel))
                    return request.FormSnapshot.ProductTypeLabel;
            }
            catch (JsonException) { }
        }
        return application.ValidationProductTypeCode ?? string.Empty;
    }

    private static (IReadOnlyList<SelectedVerificationTestItemDto> Items, string? Message) SelectedItems(
        VerificationApplication application, IReadOnlyList<ModuleRecordTask> tasks)
    {
        if (string.IsNullOrWhiteSpace(application.ValidationFormPayloadJson))
            return ([], "原申請單沒有測試項目快照，無法確認當時勾選的項目。");
        try
        {
            var request = JsonSerializer.Deserialize<ValidationRequestSubmissionDto>(
                application.ValidationFormPayloadJson, JsonOptions);
            if (request?.FormSnapshot?.TestCategories is null || request.SelectedTestItemCodes is null ||
                request.SelectedTestItemCodes.Count == 0)
                return ([], "原申請單測試項目快照缺少或不完整，無法確認當時勾選的項目。");
            if (request.FormSnapshot.TestCategories.Any(category => category.TestItems is null))
                return ([], "原申請單測試項目快照缺少或不完整，無法確認當時勾選的項目。");

            var selected = request.SelectedTestItemCodes
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var entries = request.FormSnapshot.TestCategories
                .SelectMany(category => category.TestItems.Select(item => (category, item)))
                .Where(x => selected.Contains(x.item.Code)).ToList();
            if (selected.Count == 0 || entries.Count != selected.Count ||
                entries.Select(x => x.item.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Count)
                return ([], "原申請單勾選項目與快照不一致，無法可靠顯示測試項目。");

            var items = entries.OrderBy(x => x.category.DisplayOrder).ThenBy(x => x.item.DisplayOrder)
                .Select(entry =>
                {
                    var matches = tasks.Where(task => string.Equals(
                        task.SourceTestItemCode, entry.item.Code, StringComparison.OrdinalIgnoreCase)).ToList();
                    var task = matches.Count == 1 ? matches[0] : null;
                    var values = request.TestParameterValues?.FirstOrDefault(x => string.Equals(
                        x.Key, entry.item.Code, StringComparison.OrdinalIgnoreCase)).Value;
                    var labels = FlattenParameters(entry.item.Parameters ?? [])
                        .GroupBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
                    return new SelectedVerificationTestItemDto
                    {
                        Code = entry.item.Code,
                        Name = entry.item.DisplayLabel,
                        Category = entry.category.DisplayLabel,
                        TaskId = task?.Id,
                        TaskStatus = task?.Status,
                        Parameters = values is null ? [] : values
                            .Where(x => x.Value is not null && x.Value.Any(v => !string.IsNullOrWhiteSpace(v)))
                            .Select(x => new VerificationTestParameterDto(
                                labels.TryGetValue(x.Key, out var definition) ? definition.DisplayLabel : x.Key,
                                string.Equals(definition?.FieldType, "SensitiveText", StringComparison.OrdinalIgnoreCase)
                                    ? ["敏感資料"] : x.Value.Where(v => !string.IsNullOrWhiteSpace(v)).ToList()))
                            .ToList()
                    };
                }).ToList();
            return (items, null);
        }
        catch (JsonException)
        {
            return ([], "原申請單測試項目快照無法解析，無法確認當時勾選的項目。");
        }
    }

    private static IEnumerable<ValidationParameterDto> FlattenParameters(
        IEnumerable<ValidationParameterDto> parameters)
    {
        foreach (var parameter in parameters)
        {
            yield return parameter;
            foreach (var child in FlattenParameters(parameter.Children ?? [])) yield return child;
        }
    }

    private static int Progress(IReadOnlyCollection<ModuleRecordTask> tasks) =>
        tasks.Count == 0 ? 0 : (int)Math.Round(tasks.Count(x => IsStatus(x.Status, "Close")) * 100d / tasks.Count);
    private static bool IsStatus(string? actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    private static string NormalizeStatus(string? value) =>
        Statuses.FirstOrDefault(x => string.Equals(x, value?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException("Status must be Open, Close, or Hold.");
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
