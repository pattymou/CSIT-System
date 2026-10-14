using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using System.Security.Claims;

namespace SIT.DepartmentSystem.Web.Services.Approval;

public sealed record OwnApprovalUploadDto(Guid Id, string FileName, DateTime SubmittedAt,
    ApprovalStatus Status, string? RejectReason, bool IsTestReport);

/// <summary>Only the uploader's non-formal uploads for an existing Case or Task.</summary>
public sealed class ApprovalContextUploads(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<IReadOnlyList<OwnApprovalUploadDto>> ForCaseAsync(
        ClaimsPrincipal user, string moduleCode, Guid recordId, Guid caseId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.ModuleRecordCases.AsNoTracking()
            .Where(x => x.Id == caseId && x.RecordId == recordId && x.Record.Module.Code == moduleCode)
            .Select(x => new { x.CaseNo }).SingleAsync(cancellationToken);
        var targets = new[]
        {
            new ThreeLevelUploadTarget(ThreeLevelUploadKind.CaseAttachment, caseId),
            new ThreeLevelUploadTarget(ThreeLevelUploadKind.CaseAttachmentByNo, recordId, item.CaseNo)
        };
        return await ListMineAsync(db, user, moduleCode, targets, cancellationToken);
    }

    public async Task<IReadOnlyList<OwnApprovalUploadDto>> ForTaskAsync(
        ClaimsPrincipal user, string moduleCode, Guid recordId, Guid caseId, Guid taskId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.ModuleRecordTasks.AsNoTracking()
            .Where(x => x.Id == taskId && x.CaseId == caseId &&
                x.Case.RecordId == recordId && x.Case.Record.Module.Code == moduleCode)
            .Select(x => new { x.TaskNo }).SingleAsync(cancellationToken);
        var fileIds = await db.ModuleCaseFiles.AsNoTracking()
            .Where(x => x.TaskId == taskId && x.FileKind == ModuleCaseFileKinds.TestReport)
            .Select(x => x.Id).ToListAsync(cancellationToken);
        var targets = new List<ThreeLevelUploadTarget>
        {
            new(ThreeLevelUploadKind.TaskAttachment, taskId),
            new(ThreeLevelUploadKind.TaskAttachmentByNo, caseId, item.TaskNo),
            new(ThreeLevelUploadKind.TestReport, taskId),
            new(ThreeLevelUploadKind.TestReportByTaskNo, caseId, item.TaskNo)
        };
        targets.AddRange(fileIds.Select(x => new ThreeLevelUploadTarget(ThreeLevelUploadKind.TestReportVersion, x)));
        return await ListMineAsync(db, user, moduleCode, targets, cancellationToken);
    }

    private static async Task<IReadOnlyList<OwnApprovalUploadDto>> ListMineAsync(
        AppDbContext db, ClaimsPrincipal user, string moduleCode,
        IReadOnlyList<ThreeLevelUploadTarget> targets, CancellationToken cancellationToken)
    {
        var account = ApprovalIdentity.Account(user);
        var keys = targets.Select(x => (x.ContextType, x.ContextId)).ToHashSet();
        var contextIds = targets.Select(x => x.ContextId).Distinct().ToArray();
        var rows = await db.PendingSubmissions.AsNoTracking()
            .Where(x => x.SubmittedByAccount == account && x.SourceModuleCode == moduleCode &&
                x.Status != ApprovalStatus.Committed && contextIds.Contains(x.ContextId))
            .OrderByDescending(x => x.SubmittedAt).Take(200).ToListAsync(cancellationToken);
        return rows.Where(x => keys.Contains((x.ContextType, x.ContextId)))
            .Select(x => new OwnApprovalUploadDto(x.Id, x.OriginalFileName ?? "-", x.SubmittedAt,
                x.Status, x.RejectReason, x.ContextType is nameof(ThreeLevelUploadKind.TestReport)
                    or nameof(ThreeLevelUploadKind.TestReportByTaskNo)
                    or nameof(ThreeLevelUploadKind.TestReportVersion)))
            .ToList();
    }
}
