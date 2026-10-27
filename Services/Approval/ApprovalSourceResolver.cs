using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Services.Approval;

/// <summary>Resolves upload ownership from trusted domain IDs; upload controllers must not accept a client MenuCode.</summary>
public sealed class ApprovalSourceResolver(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<string> GetModuleCodeAsync(ThreeLevelUploadTarget target,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return target.Kind switch
        {
            ThreeLevelUploadKind.CaseAttachment or ThreeLevelUploadKind.TaskAttachmentByNo
                or ThreeLevelUploadKind.TestReportByTaskNo =>
                await db.ModuleRecordCases.AsNoTracking().Where(x => x.Id == target.Id)
                    .Select(x => x.Record.Module.Code).SingleAsync(cancellationToken),
            ThreeLevelUploadKind.CaseAttachmentByNo =>
                await db.ModuleRecords.AsNoTracking().Where(x => x.Id == target.Id)
                    .Select(x => x.Module.Code).SingleAsync(cancellationToken),
            ThreeLevelUploadKind.TaskAttachment or ThreeLevelUploadKind.TestReport =>
                await db.ModuleRecordTasks.AsNoTracking().Where(x => x.Id == target.Id)
                    .Select(x => x.Case.Record.Module.Code).SingleAsync(cancellationToken),
            ThreeLevelUploadKind.TestReportVersion =>
                await db.ModuleCaseFiles.AsNoTracking().Where(x => x.Id == target.Id)
                    .Select(x => x.Record.Module.Code).SingleAsync(cancellationToken),
            _ => throw new InvalidOperationException("Unsupported ThreeLevel upload kind.")
        };
    }

    public async Task<ApprovalSourceIdentity> ForRecordAsync(Guid recordId, string caseNo,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(caseNo)) throw new InvalidOperationException("CaseNo 不可為空。");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var record = await db.ModuleRecords.AsNoTracking().Where(x => x.Id == recordId)
            .Select(x => new { x.Module.Code, x.Department, x.Team }).SingleAsync(cancellationToken);
        return await ForRecordContextAsync(db, record.Code, "Case", recordId.ToString("N"),
            caseNo, record.Department, record.Team, cancellationToken);
    }

    public async Task<ApprovalSourceIdentity> ForCaseTaskNoAsync(Guid caseId, string taskNo,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(taskNo)) throw new InvalidOperationException("TaskNo 不可為空。");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.ModuleRecordCases.AsNoTracking().Where(x => x.Id == caseId)
            .Select(x => new { x.Record.Module.Code, x.Record.Department, x.Record.Team, x.CaseNo })
            .SingleAsync(cancellationToken);
        return await ForRecordContextAsync(db, item.Code, "Task", caseId.ToString("N"),
            $"{item.CaseNo} / {taskNo}", item.Department, item.Team, cancellationToken);
    }

    public async Task<ApprovalSourceIdentity> ForFileAsync(Guid fileId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.ModuleCaseFiles.AsNoTracking()
            .Where(x => x.Id == fileId && x.FileKind == ModuleCaseFileKinds.TestReport)
            .Select(x => new { x.Record.Module.Code, x.Record.Department, x.Record.Team, x.CaseNo, x.TaskNo })
            .SingleAsync(cancellationToken);
        return await ForRecordContextAsync(db, item.Code, "TestReportVersion", fileId.ToString("N"),
            $"{item.CaseNo} / {item.TaskNo}", item.Department, item.Team, cancellationToken);
    }

    public async Task<ApprovalSourceIdentity> ForTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var context = await db.ModuleRecordTasks.AsNoTracking()
            .Where(x => x.Id == taskId)
            .Select(x => new { x.Case.Record.Module.Code, x.Case.Record.Department, x.Case.Record.Team,
                x.Case.CaseNo, x.TaskNo })
            .SingleAsync(cancellationToken);
        return await ForRecordContextAsync(db, context.Code, "Task", taskId.ToString("N"),
            $"{context.CaseNo} / {context.TaskNo}", context.Department, context.Team, cancellationToken);
    }

    public async Task<ApprovalSourceIdentity> ForCaseAsync(Guid caseId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var context = await db.ModuleRecordCases.AsNoTracking()
            .Where(x => x.Id == caseId)
            .Select(x => new { x.Record.Module.Code, x.Record.Department, x.Record.Team, x.CaseNo })
            .SingleAsync(cancellationToken);
        return await ForRecordContextAsync(db, context.Code, "Case", caseId.ToString("N"),
            context.CaseNo, context.Department, context.Team, cancellationToken);
    }

    public async Task<ApprovalSourceIdentity> ForAssetAsync(string moduleCode, string apparatusId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var asset = await db.Apparatuses.AsNoTracking()
            .Where(x => x.ModuleCode == moduleCode && x.Id == apparatusId)
            .Select(x => new { x.Id, x.Name, x.ModuleCode, x.OwnerTeamOptionId, x.DepartmentOptionId })
            .SingleAsync(cancellationToken);
        var team = asset.OwnerTeamOptionId.HasValue
            ? await db.SystemOptions.AsNoTracking()
                .Where(x => x.Id == asset.OwnerTeamOptionId && x.Category == SystemOptionCategories.Team && x.IsEnabled)
                .Select(x => new { x.Id, x.Value }).SingleOrDefaultAsync(cancellationToken)
            : null;
        var department = asset.DepartmentOptionId.HasValue
            ? await db.SystemOptions.AsNoTracking()
                .Where(x => x.Id == asset.DepartmentOptionId && x.Category == SystemOptionCategories.Department && x.IsEnabled)
                .Select(x => x.Value).SingleOrDefaultAsync(cancellationToken)
            : null;
        return new ApprovalSourceIdentity(asset.ModuleCode, "Asset", apparatusId,
            string.IsNullOrWhiteSpace(asset.Name) ? asset.Id : $"{asset.Id} / {asset.Name}",
            department, team?.Id, team?.Value);
    }

    public async Task<string> GetAssetModuleCodeAsync(string apparatusId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Apparatuses.AsNoTracking().Where(x => x.Id == apparatusId)
            .Select(x => x.ModuleCode).SingleAsync(cancellationToken);
    }

    private static async Task<ApprovalSourceIdentity> ForRecordContextAsync(AppDbContext db, string moduleCode,
        string contextType, string contextId, string display, string? department, string? teamCode,
        CancellationToken cancellationToken)
    {
        var team = string.IsNullOrWhiteSpace(teamCode) ? null : await db.SystemOptions.AsNoTracking()
            .Where(x => x.Category == SystemOptionCategories.Team && x.IsEnabled &&
                x.Value.ToLower() == teamCode.ToLower())
            .Select(x => new { x.Id, x.Value }).SingleOrDefaultAsync(cancellationToken);
        return new ApprovalSourceIdentity(moduleCode, contextType, contextId, display,
            department, team?.Id, team?.Value);
    }
}

public sealed class ApprovalMenuResolver(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<MenuItem> ResolveAsync(string moduleCode, CancellationToken cancellationToken = default)
        => await ResolveAsync(moduleCode, null, cancellationToken);

    public async Task<MenuItem> ResolveAsync(string moduleCode, string? sourceMenuCode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(moduleCode))
            throw new InvalidOperationException("來源 ModuleCode 不可空白。");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.MenuItems.AsNoTracking().Where(x => x.IsEnabled);
        var matches = string.IsNullOrWhiteSpace(sourceMenuCode)
            ? await query.Where(x => x.ModuleCode != null && x.ModuleCode.ToLower() == moduleCode.ToLower())
                .Take(2).ToListAsync(cancellationToken)
            : await query.Where(x => x.Code.ToLower() == sourceMenuCode.ToLower())
                .Take(2).ToListAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(sourceMenuCode) &&
            (matches.Count != 1 || !ApprovalSourceMenuBinding.Matches(
                matches[0].Code, matches[0].ModuleCode, moduleCode)))
            throw new InvalidOperationException(
                "測試報告的來源功能設定無法唯一判定，上傳已停止；請聯絡系統管理員確認「驗證申請專區」選單設定。");
        if (matches.Count != 1 || string.IsNullOrWhiteSpace(matches[0].Code))
            throw new InvalidOperationException("來源 Menu Code 無法唯一判定，主管審核已停止。");
        var duplicateCodes = await db.MenuItems.AsNoTracking()
            .CountAsync(x => x.Code.ToLower() == matches[0].Code.ToLower(), cancellationToken);
        if (duplicateCodes != 1)
            throw new InvalidOperationException("來源 Menu Code 重複，主管審核已停止。");
        return matches[0];
    }
}
