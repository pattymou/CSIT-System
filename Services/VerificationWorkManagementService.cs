using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Services;

public interface IVerificationWorkManagementService
{
    Task<IReadOnlyList<VerificationWorkListItemDto>> ListAsync(CancellationToken cancellationToken = default);
    Task<VerificationWorkDetailDto?> GetAsync(Guid recordId, CancellationToken cancellationToken = default);
    Task UpdateRecordStatusAsync(Guid recordId, UpdateVerificationWorkStatusRequest request, CancellationToken cancellationToken = default);
    Task UpdateTaskAsync(Guid taskId, UpdateVerificationWorkTaskRequest request, CancellationToken cancellationToken = default);
}

public sealed class VerificationWorkManagementService(
    AppDbContext db,
    IAssignableEngineerDirectory engineerDirectory,
    IBusinessWriteAuthorizationGuard businessWrite) : IVerificationWorkManagementService
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
        var reportsByTask = reports.GroupBy(x => x.TaskId!.Value).ToDictionary(x => x.Key, x => x.ToList());
        var list = ToListItem(application);
        return new VerificationWorkDetailDto
        {
            ApplicationId = list.ApplicationId,
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
                                CreatedAt = file.CreatedAt
                            }).ToList()
                    })).ToList()
        };
    }

    public async Task UpdateRecordStatusAsync(
        Guid recordId,
        UpdateVerificationWorkStatusRequest request,
        CancellationToken cancellationToken = default)
    {
        await businessWrite.DemandAsync(cancellationToken);
        var status = NormalizeStatus(request.Status);
        var record = await GetAcceptedRecordAsync(recordId, cancellationToken);
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

    private static int Progress(IReadOnlyCollection<ModuleRecordTask> tasks) =>
        tasks.Count == 0 ? 0 : (int)Math.Round(tasks.Count(x => IsStatus(x.Status, "Close")) * 100d / tasks.Count);
    private static bool IsStatus(string? actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    private static string NormalizeStatus(string? value) =>
        Statuses.FirstOrDefault(x => string.Equals(x, value?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException("Status must be Open, Close, or Hold.");
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
