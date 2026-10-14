using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Services.Approval;

namespace ApprovalFramework.Tests;

public sealed class ApprovalContextNavigationTests
{
    [Theory]
    [InlineData(ThreeLevelUploadKind.CaseAttachment, false)]
    [InlineData(ThreeLevelUploadKind.CaseAttachmentByNo, false)]
    [InlineData(ThreeLevelUploadKind.TaskAttachment, true)]
    [InlineData(ThreeLevelUploadKind.TaskAttachmentByNo, true)]
    [InlineData(ThreeLevelUploadKind.TestReport, true)]
    [InlineData(ThreeLevelUploadKind.TestReportByTaskNo, true)]
    [InlineData(ThreeLevelUploadKind.TestReportVersion, true)]
    public async Task ThreeLevelContextLinksToExistingCaseOrTask(
        ThreeLevelUploadKind kind, bool taskContext)
    {
        await using var db = NewDb();
        var moduleId = Guid.NewGuid();
        var recordId = Guid.NewGuid();
        var caseId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        db.Modules.Add(new ModuleEntity { Id = moduleId, Code = "verification" });
        db.ModuleRecords.Add(new ModuleRecord { Id = recordId, ModuleId = moduleId,
            RecordNo = "DEV-P01-A3F2", Name = "DEV-P01-A3F2" });
        db.ModuleRecordCases.Add(new ModuleRecordCase { Id = caseId, RecordId = recordId,
            CaseNo = "C1", Name = "Case-01" });
        db.ModuleRecordTasks.Add(new ModuleRecordTask { Id = taskId, CaseId = caseId,
            TaskNo = "T1", Name = "Task-01" });
        db.ModuleCaseFiles.Add(new ModuleCaseFile { Id = fileId, RecordId = recordId,
            CaseId = caseId, TaskId = taskId, CaseNo = "C1", TaskNo = "T1",
            FileKind = ModuleCaseFileKinds.TestReport, FileName = "report-01.pdf" });
        await db.SaveChangesAsync();

        var target = kind switch
        {
            ThreeLevelUploadKind.CaseAttachment => new ThreeLevelUploadTarget(kind, caseId),
            ThreeLevelUploadKind.CaseAttachmentByNo => new ThreeLevelUploadTarget(kind, recordId, "C1"),
            ThreeLevelUploadKind.TaskAttachmentByNo or ThreeLevelUploadKind.TestReportByTaskNo =>
                new ThreeLevelUploadTarget(kind, caseId, "T1"),
            ThreeLevelUploadKind.TestReportVersion => new ThreeLevelUploadTarget(kind, fileId),
            _ => new ThreeLevelUploadTarget(kind, taskId)
        };
        var link = await ApprovalContextNavigation.ResolveAsync(db, Submission(
            "verification", target.ContextType, target.ContextId));
        Assert.NotNull(link);
        Assert.Equal(taskContext
            ? $"/modules/verification/{recordId}/cases/{caseId}/tasks/{taskId}"
            : $"/modules/verification/{recordId}/cases/{caseId}", link.Route);
        Assert.Equal(taskContext ? "DEV-P01-A3F2 / Case-01 / Task-01"
            : "DEV-P01-A3F2 / Case-01", link.Display);
    }

    [Theory]
    [InlineData("equipment")]
    [InlineData("goods")]
    public async Task AssetContextLinksOnlyToMatchingModule(string module)
    {
        await using var db = NewDb();
        db.Apparatuses.Add(new Apparatus { Id = "DEV-EQ-A3F2", ModuleCode = module,
            Name = "Photo unit" });
        await db.SaveChangesAsync();
        var link = await ApprovalContextNavigation.ResolveAsync(db,
            Submission(module, "Asset", "DEV-EQ-A3F2"));
        Assert.Equal($"/assets/{module}/DEV-EQ-A3F2", link?.Route);
        Assert.Equal("Photo unit", link?.Display);
        Assert.Null(await ApprovalContextNavigation.ResolveAsync(db,
            Submission(module == "equipment" ? "goods" : "equipment", "Asset", "DEV-EQ-A3F2")));
    }

    [Fact]
    public async Task MissingOrMalformedContextHasNoLink()
    {
        await using var db = NewDb();
        Assert.Null(await ApprovalContextNavigation.ResolveAsync(db,
            Submission("verification", "CaseAttachment", Guid.NewGuid().ToString("N"))));
        Assert.Null(await ApprovalContextNavigation.ResolveAsync(db,
            Submission("verification", "CaseAttachment", "untrusted-route")));
        Assert.Null(await ApprovalContextNavigation.ResolveAsync(db,
            Submission("equipment", "Asset", "javascript:alert(1)")));
    }

    private static AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static PendingSubmission Submission(string module, string type, string id) =>
        PendingSubmission.Create(Guid.NewGuid(), Guid.NewGuid().ToString("N"), module, module,
            "dev-engineer", "DEV TEST", Guid.NewGuid(), "DEV-TEAM", type, id,
            null, null, DateTime.UtcNow);
}
