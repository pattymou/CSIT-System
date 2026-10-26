using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services;

namespace ApprovalFramework.Tests;

public sealed class VerificationTwoLevelManagementTests
{
    [Fact]
    public async Task Provisioner_CreatesOnlySelectedSnapshotItems_AndRetryIsIdempotent()
    {
        await using var db = NewDb();
        var application = SubmittedApplication("VA-001", "MODEL-X", Submission(["WIFI", "BT"]));
        var record = Record("REC-001", "MODEL-X");
        db.AddRange(application, record);
        await db.SaveChangesAsync();
        var service = new VerificationWorkItemProvisioner(db);

        await service.EnsureAsync(application, record, DateTime.UtcNow);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await service.EnsureAsync(application, record, DateTime.UtcNow);
        await db.SaveChangesAsync();

        var cases = await db.ModuleRecordCases.Include(x => x.Tasks).Where(x => x.RecordId == record.Id).ToListAsync();
        Assert.Equal(2, cases.Count);
        var tasks = cases.SelectMany(x => x.Tasks).ToList();
        Assert.Equal(2, tasks.Count);
        Assert.Equal(["BT", "WIFI"], tasks.Select(x => x.SourceTestItemCode!).OrderBy(x => x).ToArray());
        Assert.DoesNotContain(tasks, x => x.SourceTestItemCode == "GPS");
        Assert.All(tasks, x => Assert.Equal("Open", x.Status));
        Assert.All(tasks, x => Assert.Equal(record.ExpectedEndDate, x.ExpectedEndDate));
        Assert.Contains("5G", tasks.Single(x => x.SourceTestItemCode == "WIFI").SourceTestParametersJson);
    }

    [Fact]
    public async Task Provisioner_KeepsSameModelApplicationsSeparatedByRecordId()
    {
        await using var db = NewDb();
        var first = SubmittedApplication("VA-101", "SAME-MODEL", Submission(["WIFI"]));
        var second = SubmittedApplication("VA-102", "SAME-MODEL", Submission(["WIFI"]));
        var firstRecord = Record("REC-101", "SAME-MODEL");
        var secondRecord = Record("REC-102", "SAME-MODEL");
        db.AddRange(first, second, firstRecord, secondRecord);
        await db.SaveChangesAsync();
        var service = new VerificationWorkItemProvisioner(db);

        await service.EnsureAsync(first, firstRecord, DateTime.UtcNow);
        await service.EnsureAsync(second, secondRecord, DateTime.UtcNow);
        await db.SaveChangesAsync();

        Assert.Single(await db.ModuleRecordTasks.Where(x => x.Case.RecordId == firstRecord.Id).ToListAsync());
        Assert.Single(await db.ModuleRecordTasks.Where(x => x.Case.RecordId == secondRecord.Id).ToListAsync());
    }

    [Fact]
    public async Task Management_ValidatesAssignmentCompletionAndRecordClose()
    {
        await using var db = NewDb();
        var (application, record, tasks) = AcceptedWork("VA-201", "MODEL-Y", 2);
        db.AddRange(application, record);
        await db.SaveChangesAsync();
        var directory = new FakeEngineerDirectory("engineer1");
        var service = new VerificationWorkManagementService(db, directory, new AllowWrite());

        await service.UpdateRecordStatusAsync(record.Id, new UpdateVerificationWorkStatusRequest { Status = "Hold" });
        Assert.Equal("Hold", (await db.ModuleRecords.FindAsync(record.Id))!.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateRecordStatusAsync(
            record.Id, new UpdateVerificationWorkStatusRequest { Status = "Close" }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateTaskAsync(tasks[0].Id,
            new UpdateVerificationWorkTaskRequest { Status = "Open", AssignEngineer = "outsider" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateTaskAsync(tasks[0].Id,
            new UpdateVerificationWorkTaskRequest { Status = "Close", AssignEngineer = "engineer1" }));

        var completed = new DateOnly(2026, 11, 8);
        foreach (var task in tasks)
            await service.UpdateTaskAsync(task.Id, new UpdateVerificationWorkTaskRequest
            {
                Status = "Close", AssignEngineer = "engineer1", ActualCompletionDate = completed, Note = "done"
            });
        await service.UpdateRecordStatusAsync(record.Id, new UpdateVerificationWorkStatusRequest { Status = "Close" });

        db.ChangeTracker.Clear();
        var saved = await db.ModuleRecords.Include(x => x.Cases).ThenInclude(x => x.Tasks).SingleAsync(x => x.Id == record.Id);
        Assert.Equal("Close", saved.Status);
        Assert.Equal(100, saved.Progress);
        Assert.All(saved.Cases.SelectMany(x => x.Tasks), x =>
        {
            Assert.Equal("engineer1", x.AssignEngineer);
            Assert.Equal(completed, x.ActualCompletionDate);
            Assert.Equal("Close", x.Status);
        });

        await service.UpdateTaskAsync(tasks[0].Id, new UpdateVerificationWorkTaskRequest
        {
            Status = "Open", AssignEngineer = "engineer1", ActualCompletionDate = completed
        });
        Assert.Equal("Open", (await db.ModuleRecords.FindAsync(record.Id))!.Status);
    }

    [Fact]
    public async Task Management_DeniesWritesBeforeChangingRecordOrTask()
    {
        await using var db = NewDb();
        var (application, record, tasks) = AcceptedWork("VA-251", "MODEL-DENIED", 1);
        db.AddRange(application, record);
        await db.SaveChangesAsync();
        var service = new VerificationWorkManagementService(
            db, new FakeEngineerDirectory("engineer1"), new DenyWrite());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.UpdateRecordStatusAsync(
            record.Id, new UpdateVerificationWorkStatusRequest { Status = "Hold" }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.UpdateTaskAsync(
            tasks[0].Id, new UpdateVerificationWorkTaskRequest
            {
                Status = "Close", AssignEngineer = "engineer1",
                ActualCompletionDate = new DateOnly(2026, 11, 8)
            }));

        Assert.Equal("Open", (await db.ModuleRecords.FindAsync(record.Id))!.Status);
        Assert.Equal("Open", (await db.ModuleRecordTasks.FindAsync(tasks[0].Id))!.Status);
        Assert.Null((await db.ModuleRecordTasks.FindAsync(tasks[0].Id))!.AssignEngineer);
    }

    [Fact]
    public async Task Detail_ReturnsReportVersions_AndFlagsHistoricalMissingWorkWithoutChangingIt()
    {
        await using var db = NewDb();
        var (application, record, tasks) = AcceptedWork("VA-301", "MODEL-Z", 1);
        var documentId = Guid.NewGuid();
        db.AddRange(application, record, new ModuleCaseFile
        {
            Id = Guid.NewGuid(), RecordId = record.Id, CaseId = tasks[0].CaseId, CaseNo = "CASE",
            TaskId = tasks[0].Id, TaskNo = tasks[0].TaskNo, FileName = "report-v1.pdf", FilePath = "x",
            FileSize = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, DocumentId = documentId,
            VersionNo = 1, FileKind = ModuleCaseFileKinds.TestReport, ReviewStatus = TestReportReviewStatuses.Approved
        }, new ModuleCaseFile
        {
            Id = Guid.NewGuid(), RecordId = record.Id, CaseId = tasks[0].CaseId, CaseNo = "CASE",
            TaskId = tasks[0].Id, TaskNo = tasks[0].TaskNo, FileName = "report-v2.pdf", FilePath = "y",
            FileSize = 1, CreatedAt = DateTime.UtcNow.AddMinutes(1), UpdatedAt = DateTime.UtcNow.AddMinutes(1),
            DocumentId = documentId, VersionNo = 2, FileKind = ModuleCaseFileKinds.TestReport, IsFinal = true
        });
        var oldApplication = SubmittedApplication("VA-OLD", "OLD", Submission(["WIFI"]));
        var oldRecord = Record("REC-OLD", "OLD");
        oldApplication.Accept(oldRecord.Id, new DateOnly(2026, 12, 1), "leader", DateTime.UtcNow);
        db.AddRange(oldApplication, oldRecord);
        await db.SaveChangesAsync();
        var service = new VerificationWorkManagementService(db, new FakeEngineerDirectory(), new AllowWrite());

        var detail = await service.GetAsync(record.Id);
        var list = await service.ListAsync();

        Assert.NotNull(detail);
        Assert.Equal([2, 1], detail!.Tasks.Single().Reports.Select(x => x.VersionNo).ToArray());
        Assert.True(list.Single(x => x.RecordId == oldRecord.Id).RequiresSetupReview);
        Assert.Empty(await db.ModuleRecordCases.Where(x => x.RecordId == oldRecord.Id).ToListAsync());
    }

    [Fact]
    public void AcceptAndDirectApprovalShareTheTransactionalProvisioningPath()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "Services", "Implementations", "VerificationApplicationService.cs"));
        Assert.Contains("await CreateRecordAndAcceptAsync(entity, directApprovalDate", source);
        Assert.Contains("await CreateRecordAndAcceptAsync(entity, expectedEndDate", source);
        Assert.Contains("await _workItems.EnsureAsync(entity, moduleRecord, now, cancellationToken)", source);
    }

    private static ValidationRequestSubmissionDto Submission(string[] selected)
    {
        var version = Guid.NewGuid();
        var radio = new ValidationTestCategoryDto(Guid.NewGuid(), "RADIO", "Radio", true, true, 1,
        [
            Item("WIFI", "Wi-Fi", 1), Item("GPS", "GPS", 2)
        ]);
        var bluetooth = new ValidationTestCategoryDto(Guid.NewGuid(), "CONNECTIVITY", "Connectivity", true, true, 2,
        [
            Item("BT", "Bluetooth", 1)
        ]);
        return new ValidationRequestSubmissionDto
        {
            TemplateVersionId = version, TemplateVersion = 3, ProductTypeCode = "PRODUCT-A",
            SelectedTestItemCodes = selected.ToHashSet(StringComparer.OrdinalIgnoreCase),
            TestParameterValues = new(StringComparer.OrdinalIgnoreCase)
            {
                ["WIFI"] = new(StringComparer.OrdinalIgnoreCase) { ["BAND"] = ["5G"] }
            },
            FormSnapshot = new ValidationRequestPreviewDto("VALIDATION", version, 3, "PRODUCT-A", "Product A", [], [radio, bluetooth])
        };
    }

    private static ValidationTestItemDto Item(string code, string label, int order) =>
        new(Guid.NewGuid(), Guid.NewGuid(), code, label, $"{label} note", null, false, true, false, order, []);

    private static VerificationApplication SubmittedApplication(
        string number, string model, ValidationRequestSubmissionDto submission)
    {
        var department = Guid.NewGuid(); var team = Guid.NewGuid(); var now = DateTime.UtcNow;
        var application = VerificationApplication.CreateDraft(Guid.NewGuid(), number, department, team,
            "applicant", "Applicant", "a@example.test", "RD", null,
            new VerificationApplicationContent { ProjectName = $"Project {number}", ProductModel = model, Customer = "Customer" }, now);
        application.SetValidationRequest(submission.TemplateVersionId, submission.TemplateVersion,
            submission.ProductTypeCode, JsonSerializer.Serialize(submission, new JsonSerializerOptions(JsonSerializerDefaults.Web)), now);
        application.Submit(new VerificationApplicationRouting(department, "DA40", "DA40", team, "TEAM-A", "Team A",
            "verification", "leader", "Leader"), now);
        return application;
    }

    private static ModuleRecord Record(string number, string model) => new()
    {
        Id = Guid.NewGuid(), ModuleId = Guid.NewGuid(), RecordNo = number, Name = $"Project {number}",
        CustomerProductName = model, Status = "Open", Team = "TEAM-A", Location = "台北",
        ExpectedEndDate = new DateOnly(2026, 12, 1), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };

    private static (VerificationApplication Application, ModuleRecord Record, List<ModuleRecordTask> Tasks)
        AcceptedWork(string number, string model, int taskCount)
    {
        var application = SubmittedApplication(number, model, Submission(["WIFI"]));
        var record = Record($"REC-{number}", model);
        var recordCase = new ModuleRecordCase
        {
            Id = Guid.NewGuid(), RecordId = record.Id, CaseNo = $"{number}-RADIO", Name = "Radio",
            Status = "Open", SourceTestCategoryCode = "RADIO", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        var tasks = Enumerable.Range(1, taskCount).Select(index => new ModuleRecordTask
        {
            Id = Guid.NewGuid(), CaseId = recordCase.Id, TaskNo = $"{number}-TASK-{index}", Name = $"Task {index}",
            Status = "Open", SourceTestItemCode = $"ITEM-{index}", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        }).ToList();
        foreach (var task in tasks) recordCase.Tasks.Add(task);
        record.Cases.Add(recordCase);
        application.Accept(record.Id, record.ExpectedEndDate!.Value, "leader", DateTime.UtcNow);
        return (application, record, tasks);
    }

    private static AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private sealed class AllowWrite : IBusinessWriteAuthorizationGuard
    {
        public Task DemandAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class DenyWrite : IBusinessWriteAuthorizationGuard
    {
        public Task DemandAsync(CancellationToken cancellationToken = default) =>
            throw new UnauthorizedAccessException("read only");
    }

    private sealed class FakeEngineerDirectory(params string[] accounts) : IAssignableEngineerDirectory
    {
        public Task<IReadOnlyList<AssignableEngineerDto>> GetForRecordAsync(Guid recordId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AssignableEngineerDto>>(accounts.Select(x => new AssignableEngineerDto(Guid.NewGuid(), x, x, null)).ToList());
        public Task<string?> ResolveAssignableAccountAsync(Guid recordId, string? account, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(account)) return Task.FromResult<string?>(null);
            var match = accounts.FirstOrDefault(x => string.Equals(x, account, StringComparison.OrdinalIgnoreCase));
            return match is null
                ? throw new ArgumentException("Engineer is outside the allowed Product Type team.")
                : Task.FromResult<string?>(match);
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SIT.DepartmentSystem.Web.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }
}
