using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using System.Text.RegularExpressions;

namespace SIT.DepartmentSystem.Web.Services.Approval;

public sealed record ApprovalContextNavigationResult(string Display, string Route);

/// <summary>Builds links only after the submission's trusted context resolves to a matching domain row.</summary>
public static class ApprovalContextNavigation
{
    public static async Task<ApprovalContextNavigationResult?> ResolveAsync(
        AppDbContext db, PendingSubmission submission, CancellationToken cancellationToken = default)
    {
        var module = submission.SourceModuleCode;
        if (submission.ContextType == "Asset" && module is "equipment" or "goods")
        {
            var asset = await db.Apparatuses.AsNoTracking()
                .Where(x => x.Id == submission.ContextId && x.ModuleCode == module)
                .Select(x => new { x.Id, x.Name }).SingleOrDefaultAsync(cancellationToken);
            if (asset is null || !Regex.IsMatch(asset.Id, "^[A-Za-z0-9._-]+$")) return null;
            return new ApprovalContextNavigationResult(
                Name(asset.Name, asset.Id), $"/assets/{module}/{Uri.EscapeDataString(asset.Id)}");
        }

        if (module is not ("verification" or "certification")) return null;
        ThreeLevelUploadTarget target;
        try { target = ThreeLevelUploadTarget.FromContext(submission.ContextType, submission.ContextId); }
        catch { return null; }

        switch (target.Kind)
        {
            case ThreeLevelUploadKind.CaseAttachment:
                return await CaseAsync(db, module, target.Id, cancellationToken);
            case ThreeLevelUploadKind.CaseAttachmentByNo:
            {
                var caseId = await db.ModuleRecordCases.AsNoTracking()
                    .Where(x => x.RecordId == target.Id && x.CaseNo == target.Number &&
                        x.Record.Module.Code == module)
                    .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(cancellationToken);
                return caseId.HasValue ? await CaseAsync(db, module, caseId.Value, cancellationToken) : null;
            }
            case ThreeLevelUploadKind.TaskAttachment:
            case ThreeLevelUploadKind.TestReport:
                return await TaskAsync(db, module, target.Id, cancellationToken);
            case ThreeLevelUploadKind.TaskAttachmentByNo:
            case ThreeLevelUploadKind.TestReportByTaskNo:
            {
                var taskId = await db.ModuleRecordTasks.AsNoTracking()
                    .Where(x => x.CaseId == target.Id && x.TaskNo == target.Number &&
                        x.Case.Record.Module.Code == module)
                    .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(cancellationToken);
                return taskId.HasValue ? await TaskAsync(db, module, taskId.Value, cancellationToken) : null;
            }
            case ThreeLevelUploadKind.TestReportVersion:
            {
                var file = await db.ModuleCaseFiles.AsNoTracking()
                    .Where(x => x.Id == target.Id && x.FileKind == ModuleCaseFileKinds.TestReport &&
                        x.Record.Module.Code == module)
                    .Select(x => new { x.TaskId, x.CaseId, x.TaskNo })
                    .SingleOrDefaultAsync(cancellationToken);
                if (file is null) return null;
                if (file.TaskId.HasValue)
                    return await TaskAsync(db, module, file.TaskId.Value, cancellationToken);
                if (!file.CaseId.HasValue || string.IsNullOrWhiteSpace(file.TaskNo)) return null;
                var taskId = await db.ModuleRecordTasks.AsNoTracking()
                    .Where(x => x.CaseId == file.CaseId && x.TaskNo == file.TaskNo &&
                        x.Case.Record.Module.Code == module)
                    .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(cancellationToken);
                return taskId.HasValue ? await TaskAsync(db, module, taskId.Value, cancellationToken) : null;
            }
            default: return null;
        }
    }

    private static async Task<ApprovalContextNavigationResult?> CaseAsync(
        AppDbContext db, string module, Guid caseId, CancellationToken cancellationToken)
    {
        var item = await db.ModuleRecordCases.AsNoTracking()
            .Where(x => x.Id == caseId && x.Record.Module.Code == module)
            .Select(x => new { x.Id, x.RecordId, x.Name, x.CaseNo,
                ProjectName = x.Record.Name, ProjectNo = x.Record.RecordNo })
            .SingleOrDefaultAsync(cancellationToken);
        return item is null ? null : new ApprovalContextNavigationResult(
            $"{Name(item.ProjectName, item.ProjectNo)} / {Name(item.Name, item.CaseNo)}",
            $"/modules/{module}/{item.RecordId}/cases/{item.Id}");
    }

    private static async Task<ApprovalContextNavigationResult?> TaskAsync(
        AppDbContext db, string module, Guid taskId, CancellationToken cancellationToken)
    {
        var item = await db.ModuleRecordTasks.AsNoTracking()
            .Where(x => x.Id == taskId && x.Case.Record.Module.Code == module)
            .Select(x => new { x.Id, x.CaseId, x.Name, x.TaskNo,
                x.Case.RecordId, CaseName = x.Case.Name, x.Case.CaseNo,
                ProjectName = x.Case.Record.Name, ProjectNo = x.Case.Record.RecordNo })
            .SingleOrDefaultAsync(cancellationToken);
        return item is null ? null : new ApprovalContextNavigationResult(
            $"{Name(item.ProjectName, item.ProjectNo)} / {Name(item.CaseName, item.CaseNo)} / {Name(item.Name, item.TaskNo)}",
            $"/modules/{module}/{item.RecordId}/cases/{item.CaseId}/tasks/{item.Id}");
    }

    private static string Name(string? name, string fallback) =>
        string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();
}
