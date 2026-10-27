using System.Text.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using SIT.DepartmentSystem.Web.Components.Shared;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

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

        await service.UpdateRecordStatusAsync(record.Id, new UpdateVerificationWorkStatusRequest { Status = "Hold" }, Manager());
        Assert.Equal("Hold", (await db.ModuleRecords.FindAsync(record.Id))!.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateRecordStatusAsync(
            record.Id, new UpdateVerificationWorkStatusRequest { Status = "Close" }, Manager()));
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
        await service.UpdateRecordStatusAsync(record.Id, new UpdateVerificationWorkStatusRequest { Status = "Close" }, Manager());

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
            record.Id, new UpdateVerificationWorkStatusRequest { Status = "Hold" }, Manager()));
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
    public async Task RecordStatusRequiresAssignedManagerAndRejectsUnchangedStatus()
    {
        await using var db = NewDb();
        var (application, record, _) = AcceptedWork("VA-STATUS", "MODEL-STATUS", 1);
        db.AddRange(application, record);
        await db.SaveChangesAsync();
        var service = new VerificationWorkManagementService(db, new FakeEngineerDirectory(), new AllowWrite());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.UpdateRecordStatusAsync(
            record.Id, new UpdateVerificationWorkStatusRequest { Status = "Hold" }, Manager("staff")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateRecordStatusAsync(
            record.Id, new UpdateVerificationWorkStatusRequest { Status = "Open" }, Manager()));
        Assert.Equal("Open", (await db.ModuleRecords.FindAsync(record.Id))!.Status);
        await service.UpdateRecordStatusAsync(record.Id, new UpdateVerificationWorkStatusRequest { Status = "Hold" }, Manager());
        Assert.Equal("Hold", (await db.ModuleRecords.FindAsync(record.Id))!.Status);
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
    public async Task Detail_ProjectsFormalArchiveAndKmStatusFromPersistedRowsInBatches()
    {
        await using var db = NewDb();
        var (application, record, tasks) = AcceptedWork("VA-KM", "MODEL-KM", 1);
        var now = DateTime.UtcNow;
        ModuleCaseFile Report(string name, int version, string review, bool raw) => new()
        {
            Id = Guid.NewGuid(), RecordId = record.Id, CaseId = tasks[0].CaseId, CaseNo = "CASE",
            TaskId = tasks[0].Id, TaskNo = tasks[0].TaskNo, FileName = name, FilePath = "x",
            ContentType = "application/pdf", FileSize = 10, CreatedAt = now.AddMinutes(version),
            UpdatedAt = now.AddMinutes(version), DocumentId = Guid.NewGuid(), VersionNo = version,
            FileKind = ModuleCaseFileKinds.TestReport, ReviewStatus = review, IsRawDataExported = raw
        };
        var awaiting = Report("awaiting.pdf", 1, TestReportReviewStatuses.Pending, false);
        var missing = Report("missing.pdf", 2, TestReportReviewStatuses.Approved, true);
        var pending = Report("pending.pdf", 3, TestReportReviewStatuses.Approved, true);
        var completed = Report("completed.pdf", 4, TestReportReviewStatuses.Approved, true);
        var failed = Report("failed.pdf", 5, TestReportReviewStatuses.Approved, true);
        var pendingOutbox = KmIntegrationOutbox.ForCommittedTestReport(pending, now);
        var completedOutbox = KmIntegrationOutbox.ForCommittedTestReport(completed, now);
        var failedOutbox = KmIntegrationOutbox.ForCommittedTestReport(failed, now);
        var lease = Guid.NewGuid();
        completedOutbox.Claim(lease, now, TimeSpan.FromMinutes(1));
        completedOutbox.Sent(lease, "job-completed", now);
        completedOutbox.CallbackCompleted(now);
        lease = Guid.NewGuid();
        failedOutbox.Claim(lease, now, TimeSpan.FromMinutes(1));
        failedOutbox.Sent(lease, "job-failed", now);
        failedOutbox.CallbackFailed("KM_FAILED", "Bearer secret must not be shown", now);
        db.AddRange(application, record, awaiting, missing, pending, completed, failed,
            pendingOutbox, completedOutbox, failedOutbox);
        await db.SaveChangesAsync();
        var service = new VerificationWorkManagementService(db, new FakeEngineerDirectory(), new AllowWrite(),
            Options.Create(new KmIntegrationOptions { Enabled = false }));

        var reports = (await service.GetAsync(record.Id))!.Tasks.Single().Reports
            .ToDictionary(x => x.VersionNo);

        Assert.Equal("待主管審核", reports[1].FormalArchiveStatusDisplay);
        Assert.Equal("尚未送 KM", reports[1].KmAnalysisStatusDisplay);
        Assert.Equal("正式入庫完成", reports[2].FormalArchiveStatusDisplay);
        Assert.Equal("尚未建立同步工作", reports[2].KmAnalysisStatusDisplay);
        Assert.Equal("待同步（KM 未啟用）", reports[3].KmAnalysisStatusDisplay);
        Assert.Equal("完成", reports[4].KmAnalysisStatusDisplay);
        Assert.Equal("失敗", reports[5].KmAnalysisStatusDisplay);
        Assert.Equal("KM_FAILED：Sensitive error details redacted.", reports[5].KmAnalysisError);
    }

    [Fact]
    public async Task TaskCard_RendersPersistedFormalAndKmStatuses()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IJSRuntime, NoOpJsRuntime>();
        services.AddSingleton<BrowserApiClient>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var task = new VerificationWorkTaskDto
        {
            Id = Guid.NewGuid(), CaseId = Guid.NewGuid(), Name = "Wi-Fi", Category = "Radio", Status = "Open",
            Reports =
            [
                new VerificationWorkReportDto { Id = Guid.NewGuid(), FileName = "done.pdf", VersionNo = 2,
                    FormalArchiveStatus = "Completed", FormalArchiveStatusDisplay = "正式入庫完成",
                    KmAnalysisStatus = "Completed", KmAnalysisStatusDisplay = "完成" },
                new VerificationWorkReportDto { Id = Guid.NewGuid(), FileName = "waiting.pdf", VersionNo = 1,
                    FormalArchiveStatus = "PendingApproval", FormalArchiveStatusDisplay = "待主管審核",
                    KmAnalysisStatus = "NotSent", KmAnalysisStatusDisplay = "尚未送 KM" }
            ]
        };

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<VerificationWorkTaskCard>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(VerificationWorkTaskCard.Task)] = task,
                    [nameof(VerificationWorkTaskCard.RecordId)] = Guid.NewGuid() }))).ToHtmlString());

        var decoded = System.Net.WebUtility.HtmlDecode(html);
        Assert.Contains("v2", decoded);
        Assert.Contains("report-status completed", html);
        Assert.Contains("正式入庫完成", decoded);
        Assert.Contains("完成", decoded);
        Assert.Contains("report-status waiting", html);
        Assert.Contains("待主管審核", decoded);
        Assert.Contains("尚未送 KM", decoded);
        Assert.True(decoded.IndexOf("v2", StringComparison.Ordinal) < decoded.IndexOf("v1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Detail_UsesEachAcceptedApplicationSnapshotAndRejectsCrossRecordTaskAccess()
    {
        await using var db = NewDb();
        var first = SubmittedApplication("VA-FIRST", "SAME-MODEL", Submission(["WIFI"]));
        var second = SubmittedApplication("VA-SECOND", "SAME-MODEL", Submission(["BT"]));
        var firstRecord = Record("REC-FIRST", "SAME-MODEL");
        var secondRecord = Record("REC-SECOND", "SAME-MODEL");
        db.AddRange(first, second, firstRecord, secondRecord);
        await db.SaveChangesAsync();
        var provisioner = new VerificationWorkItemProvisioner(db);
        await provisioner.EnsureAsync(first, firstRecord, DateTime.UtcNow);
        await provisioner.EnsureAsync(second, secondRecord, DateTime.UtcNow);
        first.Accept(firstRecord.Id, firstRecord.ExpectedEndDate!.Value, "leader", DateTime.UtcNow);
        second.Accept(secondRecord.Id, secondRecord.ExpectedEndDate!.Value, "leader", DateTime.UtcNow);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var service = new VerificationWorkManagementService(db, new FakeEngineerDirectory("engineer1"), new AllowWrite());

        var firstDetail = (await service.GetAsync(firstRecord.Id))!;
        var secondDetail = (await service.GetAsync(secondRecord.Id))!;
        var selected = Assert.Single(firstDetail.SelectedTestItems);
        Assert.Equal("WIFI", selected.Code);
        Assert.Equal("Wi-Fi", selected.Name);
        Assert.Equal("Radio", selected.Category);
        Assert.Equal("BAND", selected.Parameters.Single().Name);
        Assert.Equal(["5G"], selected.Parameters.Single().Values);
        Assert.NotNull(selected.TaskId);
        Assert.Equal("BT", Assert.Single(secondDetail.SelectedTestItems).Code);
        Assert.Equal("VA-FIRST", firstDetail.ApplicationNo);
        Assert.Equal("VA-SECOND", secondDetail.ApplicationNo);
        Assert.DoesNotContain(firstDetail.SelectedTestItems, x => x.Code == "GPS");
        Assert.Null(await service.GetTaskAsync(secondRecord.Id, selected.TaskId!.Value));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.UpdateTaskForRecordAsync(
            secondRecord.Id, selected.TaskId.Value, new UpdateVerificationWorkTaskRequest
            { Status = "Hold", AssignEngineer = "engineer1" }));
        Assert.Equal("Open", (await db.ModuleRecordTasks.FindAsync(selected.TaskId.Value))!.Status);

        var taskDetail = (await service.GetTaskAsync(firstRecord.Id, selected.TaskId.Value))!;
        Assert.Equal(firstRecord.Id, taskDetail.RecordId);
        Assert.Equal("WIFI", taskDetail.SelectedItem.Code);
        await service.UpdateTaskForRecordAsync(firstRecord.Id, selected.TaskId.Value,
            new UpdateVerificationWorkTaskRequest { Status = "Hold", AssignEngineer = "engineer1" });
        Assert.Equal("Hold", (await db.ModuleRecordTasks.FindAsync(selected.TaskId.Value))!.Status);
    }

    [Fact]
    public async Task HistoricalAcceptedSnapshotRemainsReadOnlyWithoutCreatingTasks()
    {
        await using var db = NewDb();
        var application = SubmittedApplication("VA-HISTORY", "OLD-MODEL", Submission(["WIFI"]));
        var record = Record("REC-HISTORY", "OLD-MODEL");
        application.Accept(record.Id, record.ExpectedEndDate!.Value, "leader", DateTime.UtcNow);
        db.AddRange(application, record);
        await db.SaveChangesAsync();
        var service = new VerificationWorkManagementService(db, new FakeEngineerDirectory(), new AllowWrite());

        var detail = (await service.GetAsync(record.Id))!;
        var selected = Assert.Single(detail.SelectedTestItems);
        Assert.Equal("WIFI", selected.Code);
        Assert.Null(selected.TaskId);
        Assert.Null(selected.TaskStatus);
        Assert.Null(detail.SnapshotMessage);
        Assert.Empty(detail.Tasks);
        Assert.Empty(await db.ModuleRecordCases.Where(x => x.RecordId == record.Id).ToListAsync());
        Assert.Null(await service.GetTaskAsync(record.Id, Guid.NewGuid()));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.UpdateTaskForRecordAsync(
            record.Id, Guid.NewGuid(), new UpdateVerificationWorkTaskRequest { Status = "Hold" }));
    }

    [Fact]
    public async Task MissingSnapshotShowsDataMessageWithoutInventingItems()
    {
        await using var db = NewDb();
        var request = Submission(["WIFI"]);
        request.FormSnapshot = null;
        var application = SubmittedApplication("VA-MISSING", "OLD-MODEL", request);
        var record = Record("REC-MISSING", "OLD-MODEL");
        application.Accept(record.Id, record.ExpectedEndDate!.Value, "leader", DateTime.UtcNow);
        db.AddRange(application, record);
        await db.SaveChangesAsync();
        var service = new VerificationWorkManagementService(db, new FakeEngineerDirectory(), new AllowWrite());

        var detail = (await service.GetAsync(record.Id))!;
        Assert.Empty(detail.SelectedTestItems);
        Assert.NotNull(detail.SnapshotMessage);

        db.Entry(application).Property(x => x.ValidationFormPayloadJson).CurrentValue = "{invalid";
        await db.SaveChangesAsync();
        var malformed = (await service.GetAsync(record.Id))!;
        Assert.Empty(malformed.SelectedTestItems);
        Assert.Contains("無法解析", malformed.SnapshotMessage);
    }

    [Fact]
    public void AcceptAndDirectApprovalShareTheTransactionalProvisioningPath()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "Services", "Implementations", "VerificationApplicationService.cs"));
        Assert.Contains("await CreateRecordAndAcceptAsync(entity, directApprovalDate", source);
        Assert.Contains("await CreateRecordAndAcceptAsync(entity, expectedEndDate", source);
        Assert.Contains("await _workItems.EnsureAsync(entity, moduleRecord, now, cancellationToken)", source);
    }

    [Fact]
    public void ValidationRequestApplicationsShowsOnlyAcceptedWorkByRecordStatus()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "Components", "Pages", "ValidationRequestApplications.razor"));
        Assert.Contains("_work = await Work.ListAsync();", source);
        Assert.Contains("private string _selectedStatus = \"Open\";", source);
        Assert.Contains("string.Equals(item.Status, _selectedStatus, StringComparison.OrdinalIgnoreCase)", source);
        Assert.Contains("/validation-request-applications/work/{item.RecordId}", source);
        Assert.Equal(1, source.Split("<table ", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("ListForCsitAsync", source);
        Assert.DoesNotContain("APPLICATION WORKFLOW", source);
        Assert.DoesNotContain("Model Name 整案管理", source);
        Assert.DoesNotContain("<th>進度</th>", source);
        Assert.DoesNotContain("item.Progress", source);
        Assert.DoesNotContain("歷史資料：待確認工作項目", source);
        Assert.DoesNotContain("Task.WhenAll", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkPagesSeparateSelectionFromTaskEditingAndHistoricalReadOnlyView()
    {
        var list = File.ReadAllText(Path.Combine(RepositoryRoot(), "Components", "Pages", "VerificationWorkDetail.razor"));
        var task = File.ReadAllText(Path.Combine(RepositoryRoot(), "Components", "Pages", "VerificationWorkTaskDetail.razor"));
        var historical = File.ReadAllText(Path.Combine(RepositoryRoot(), "Components", "Pages", "VerificationWorkSelectedItem.razor"));
        Assert.Contains("_item.SelectedTestItems", list);
        Assert.Contains("selected.TaskId is Guid taskId", list);
        Assert.Contains("OpenSelectedAsync(selected)", list);
        Assert.DoesNotContain("EnableWorkAsync", list);
        Assert.DoesNotContain("VerificationWorkTaskCard", list);
        Assert.DoesNotContain("Progress%", list);
        Assert.Contains("VerificationWorkTaskCard", task);
        Assert.Contains("/api/verification-work/{RecordId}/tasks/{TaskId}", task);
        Assert.DoesNotContain("原申請測試參數", task);
        Assert.DoesNotContain("原申請未填寫測試參數", task);
        Assert.Contains("x.TaskId is null", historical);
        Assert.DoesNotContain("VerificationWorkTaskCard", historical);
        Assert.DoesNotContain("UploadFilesAsync", historical);
    }

    [Fact]
    public void TaskReportUploadLayoutKeepsActionOnOneLine()
    {
        var card = File.ReadAllText(Path.Combine(RepositoryRoot(), "Components", "Shared",
            "VerificationWorkTaskCard.razor"));
        var styles = File.ReadAllText(Path.Combine(RepositoryRoot(), "Components", "Shared",
            "VerificationWorkTaskCard.razor.css"));

        Assert.Contains("上傳測試報告", card);
        Assert.Contains("_reportError", card);
        Assert.Contains("white-space:nowrap", styles);
        Assert.Contains("min-width:8.5rem", styles);
        Assert.Contains("text-overflow:ellipsis", styles);
        Assert.Contains("min-width:0", styles);
        Assert.Contains("正式入庫", card);
        Assert.Contains("KM 分析", card);
        Assert.Contains("重新整理狀態", card);
        Assert.Contains("report-status", styles);
    }

    [Theory]
    [InlineData(1, "v1")]
    [InlineData(2, "v2")]
    [InlineData(3, "v3")]
    public void TaskReportVersionCellRendersNumericVersion(int versionNo, string expected)
    {
        var card = File.ReadAllText(Path.Combine(RepositoryRoot(), "Components", "Shared",
            "VerificationWorkTaskCard.razor"));

        Assert.Contains("<td>@($\"v{report.VersionNo}\")</td>", card);
        Assert.DoesNotContain("<td>v@report.VersionNo</td>", card);
        Assert.Equal($"<td>{expected}</td>", $"<td>v{versionNo}</td>");
    }

    [Fact]
    public void TaskStatusBadgeStylesOnlyApplyToTaskStatusBranch()
    {
        var page = File.ReadAllText(Path.Combine(RepositoryRoot(), "Components", "Pages",
            "VerificationWorkDetail.razor"));
        var styles = File.ReadAllText(Path.Combine(RepositoryRoot(), "Components", "Pages",
            "VerificationWorkDetail.razor.css"));

        Assert.Contains("class=\"work-item-state @TaskStatusClass(selected.TaskStatus)\"", page);
        Assert.Contains("\"OPEN\" => \"task-status-badge task-status-open\"", page);
        Assert.Contains("\"HOLD\" => \"task-status-badge task-status-hold\"", page);
        Assert.Contains("\"CLOSE\" => \"task-status-badge task-status-close\"", page);
        Assert.Contains("_ => string.Empty", page);
        Assert.Contains("min-width:5.25rem", styles);
        Assert.Contains("font-weight:700", styles);
        Assert.Contains("border-radius:999px", styles);
        Assert.Contains(".task-status-open", styles);
        Assert.Contains(".task-status-hold", styles);
        Assert.Contains(".task-status-close", styles);
        Assert.Contains("justify-self:start", styles);
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

    private static ClaimsPrincipal Manager(string account = "leader") => new(new ClaimsIdentity(
        [new Claim("account", account), new Claim(SystemAuthorization.AccessScopeClaim,
            SystemAuthorization.AccessScopes.CsitStaff)], "test"));

    private sealed class AllowWrite : IBusinessWriteAuthorizationGuard
    {
        public Task DemandAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class DenyWrite : IBusinessWriteAuthorizationGuard
    {
        public Task DemandAsync(CancellationToken cancellationToken = default) =>
            throw new UnauthorizedAccessException("read only");
    }

    private sealed class NoOpJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new NotSupportedException();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier,
            CancellationToken cancellationToken, object?[]? args) => throw new NotSupportedException();
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
