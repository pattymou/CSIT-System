using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Services.Approval;

/// <summary>Shared guards for formal writes and staged ThreeLevel submissions.</summary>
public static class TestReportBusinessRules
{
    public const string ExistingReportMessage = "此 Task 已有測試報告，請使用『上傳新版』。";
    public const string PendingVersionMessage = "此測試報告已有待處理版本，請先完成目前審核流程。";
    public const string PendingInitialMessage = "此 Task 已有待處理測試報告，請先完成目前審核流程。";

    public static async Task LockAsync(AppDbContext db, string key, CancellationToken cancellationToken = default)
    {
        if (!db.Database.IsRelational()) return;
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A transaction is required for the report lock.");
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction.GetDbTransaction();
        command.CommandText = "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "key";
        parameter.Value = key;
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static string TaskKey(Guid caseId, string taskNo) => $"test-report:task:{caseId:N}:{taskNo}";
    public static string DocumentKey(Guid documentId) => $"test-report:document:{documentId:N}";

    public static async Task EnsureInitialAvailableAsync(AppDbContext db, Guid caseId, string taskNo,
        Guid? taskId = null, Guid? currentSubmissionId = null, CancellationToken cancellationToken = default)
    {
        if (await db.ModuleCaseFiles.AsNoTracking().AnyAsync(x => x.FileKind == ModuleCaseFileKinds.TestReport
            && ((x.CaseId == caseId && x.TaskNo == taskNo) || (taskId != null && x.TaskId == taskId)), cancellationToken))
            throw new InvalidOperationException(ExistingReportMessage);

        var byNo = new ThreeLevelUploadTarget(ThreeLevelUploadKind.TestReportByTaskNo, caseId, taskNo).ContextId;
        var byId = taskId.HasValue
            ? new ThreeLevelUploadTarget(ThreeLevelUploadKind.TestReport, taskId.Value).ContextId : null;
        if (await db.PendingSubmissions.AsNoTracking().AnyAsync(x => x.Id != currentSubmissionId
            && (x.Status == ApprovalStatus.Pending || x.Status == ApprovalStatus.ApprovedPendingCommit
                || x.Status == ApprovalStatus.CommitFailed)
            && ((x.ContextType == nameof(ThreeLevelUploadKind.TestReportByTaskNo) && x.ContextId == byNo)
                || (byId != null && x.ContextType == nameof(ThreeLevelUploadKind.TestReport)
                    && x.ContextId == byId)), cancellationToken))
            throw new InvalidOperationException(PendingInitialMessage);
    }

    public static async Task EnsureNoPendingVersionAsync(AppDbContext db, Guid documentId,
        Guid? currentSubmissionId = null, CancellationToken cancellationToken = default)
    {
        var fileIds = (await db.ModuleCaseFiles.AsNoTracking()
            .Where(x => x.DocumentId == documentId).Select(x => x.Id)
            .ToArrayAsync(cancellationToken)).Select(x => x.ToString("N")).ToArray();
        if (await db.PendingSubmissions.AsNoTracking().AnyAsync(x => x.Id != currentSubmissionId
            && x.ContextType == nameof(ThreeLevelUploadKind.TestReportVersion)
            && fileIds.Contains(x.ContextId)
            && (x.Status == ApprovalStatus.Pending || x.Status == ApprovalStatus.ApprovedPendingCommit
                || x.Status == ApprovalStatus.CommitFailed), cancellationToken))
            throw new InvalidOperationException(PendingVersionMessage);
    }

    public static async Task ValidatePendingAsync(AppDbContext db, ThreeLevelUploadTarget target,
        Guid submissionId, CancellationToken cancellationToken = default)
    {
        if (target.Kind is ThreeLevelUploadKind.TestReport or ThreeLevelUploadKind.TestReportByTaskNo)
        {
            var task = target.Kind == ThreeLevelUploadKind.TestReport
                ? await db.ModuleRecordTasks.AsNoTracking().Where(x => x.Id == target.Id)
                    .Select(x => new { x.CaseId, x.TaskNo, TaskId = (Guid?)x.Id })
                    .SingleAsync(cancellationToken)
                : await db.ModuleRecordTasks.AsNoTracking().Where(x => x.CaseId == target.Id && x.TaskNo == target.Number)
                    .Select(x => new { x.CaseId, x.TaskNo, TaskId = (Guid?)x.Id })
                    .FirstOrDefaultAsync(cancellationToken);
            var caseId = task?.CaseId ?? target.Id;
            var taskNo = task?.TaskNo ?? target.Number ?? throw new InvalidOperationException("Task No is missing.");
            await LockAsync(db, TaskKey(caseId, taskNo), cancellationToken);
            await EnsureInitialAvailableAsync(db, caseId, taskNo, task?.TaskId, submissionId, cancellationToken);
        }
        else if (target.Kind == ThreeLevelUploadKind.TestReportVersion)
        {
            var documentId = await db.ModuleCaseFiles.AsNoTracking()
                .Where(x => x.Id == target.Id && x.FileKind == ModuleCaseFileKinds.TestReport)
                .Select(x => x.DocumentId).SingleAsync(cancellationToken);
            await LockAsync(db, DocumentKey(documentId), cancellationToken);
            await EnsureNoPendingVersionAsync(db, documentId, submissionId, cancellationToken);
        }
    }
}
