using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Controllers;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Config;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Approval;
using SIT.DepartmentSystem.Web.Services.Implementations;

namespace ApprovalFramework.Tests;

[Collection("Dev approval integration")]
public sealed class CertificationDevIntegrationTests
{
    [Theory]
    [InlineData("certification")]
    [InlineData("verification")]
    [Trait("Category", "DevIntegration")]
    public async Task DevThreeLevelPendingReviewCommitRejectRetryAndBypass(string moduleCode)
    {
        var connection = Environment.GetEnvironmentVariable("CSIT_DEV_APPROVAL_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) return;
        var connectionInfo = new NpgsqlConnectionStringBuilder(connection);
        Assert.Equal("sit_department_system_dev", connectionInfo.Database);
        Assert.True(connectionInfo.Host is "localhost" or "127.0.0.1");
        Assert.Equal("csit_runtime_dev", connectionInfo.Username);

        const string storageBase = @"D:\CSIT_Dev_Data";
        var stagingRoot = Path.Combine(storageBase, "ApprovalStaging");
        var formalRoot = Path.Combine(storageBase, "ThreeLevel", moduleCode);
        var reportRoot = Path.Combine(storageBase, "TestReports");
        var rawRoot = Path.Combine(storageBase, "RawData");
        Assert.True(Directory.Exists(stagingRoot) && Directory.Exists(formalRoot)
            && Directory.Exists(reportRoot) && Directory.Exists(rawRoot));

        var factory = new TestDbFactory(connection);
        var tag = "DEV-P01-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        string DemoFile(string name)
        {
            var dot = name.LastIndexOf('.');
            return $"{name[..dot]}-{tag[^6..]}{name[dot..]}";
        }
        var supervisorReportName = DemoFile("report-04.pdf");
        var supervisorReportV2Name = DemoFile("report-05.pdf");
        var caseNo = "Case-01-" + tag[^6..];
        var taskNo = "Task-01-" + tag[^6..];
        var bypassTaskNo = "Task-02-" + tag[^6..];
        var byNoTaskNo = "Task-03-" + tag[^6..];
        var concurrentTaskNo = "Task-04-" + tag[^6..];
        var withdrawnTaskNo = "Task-05-" + tag[^6..];
        var teamId = Guid.NewGuid();
        var recordId = Guid.NewGuid();
        var caseId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var bypassTaskId = Guid.NewGuid();
        var withdrawnTaskId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var moduleId = await db.Modules.Where(x => x.Code == moduleCode).Select(x => x.Id).SingleAsync();
            Assert.True(await db.MenuItems.AnyAsync(x => x.Code == moduleCode
                && x.ModuleCode == moduleCode && x.RequiresSupervisorApproval));
            db.SystemOptions.Add(new SystemOption { Id = teamId, Category = SystemOptionCategories.Team,
                Name = tag, Value = tag, IsEnabled = true, CreatedAt = now, UpdatedAt = now });
            db.TeamRoutings.Add(new TeamRouting { Id = Guid.NewGuid(), TeamOptionId = teamId,
                LeaderAccount = "dev-approval-leader", LeaderDisplayName = "DEV TEST Leader",
                IsEnabled = true, CreatedAt = now, UpdatedAt = now });
            db.ModuleRecords.Add(new ModuleRecord { Id = recordId, ModuleId = moduleId,
                RecordNo = tag, Name = tag, Department = "DEV TEST", Team = tag,
                CreatedAt = now, UpdatedAt = now });
            db.ModuleRecordCases.Add(new ModuleRecordCase { Id = caseId, RecordId = recordId,
                CaseNo = caseNo, Name = caseNo, CreatedAt = now, UpdatedAt = now });
            db.ModuleRecordTasks.Add(new ModuleRecordTask { Id = taskId, CaseId = caseId,
                TaskNo = taskNo, Name = taskNo, CreatedAt = now, UpdatedAt = now });
            db.ModuleRecordTasks.Add(new ModuleRecordTask { Id = bypassTaskId, CaseId = caseId,
                TaskNo = bypassTaskNo, Name = bypassTaskNo, CreatedAt = now, UpdatedAt = now });
            db.ModuleRecordTasks.Add(new ModuleRecordTask { Id = withdrawnTaskId, CaseId = caseId,
                TaskNo = withdrawnTaskNo, Name = withdrawnTaskNo, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
        }

        var sourceResolver = new ApprovalSourceResolver(factory);
        var stagingRootResolver = new TestRoot(stagingRoot);
        var staging = new ApprovalStagingStorage(stagingRootResolver);
        var rawSettings = Options.Create(new RawDataSettings { Enabled = true, NasRootPath = rawRoot });
        var fileDb = await factory.CreateDbContextAsync();
        var formal = new CaseFileService(fileDb,
            Options.Create(new UploadSettings { BasePath = formalRoot, UploadKind = "Lab" }),
            rawSettings, new RawDataExportService(rawSettings), NullLogger<CaseFileService>.Instance);
        var handler = new ThreeLevelUploadApprovalCommitHandler(moduleCode,
            sourceResolver, staging, formal, factory, fileDb);
        var otherModuleCode = moduleCode == "verification" ? "certification" : "verification";
        var otherHandler = new ThreeLevelUploadApprovalCommitHandler(otherModuleCode,
            sourceResolver, staging, formal, factory, fileDb);
        var handlers = new ApprovalCommitHandlerRegistry([handler, otherHandler]);
        var auth = new TestAuthorization();
        var policy = new ApprovalPolicyResolver(new ApprovalMenuResolver(factory), handlers,
            stagingRootResolver, auth);
        var scope = new ApprovalReviewerScopeResolver(factory, auth);
        var submissions = new ApprovalSubmissionService(factory, scope, handlers,
            stagingRootResolver, staging);
        var upload = new ThreeLevelApprovalUploadService(sourceResolver, policy,
            submissions, staging, formal);
        var worker = new ApprovalCommitWorker(factory, handlers, NullLogger<ApprovalCommitWorker>.Instance, fileDb);
        var kmDownload = new KmIntegrationController(factory, NullLogger<KmIntegrationController>.Instance);
        var engineer = User("dev-approval-engineer");
        var leader = User("dev-approval-leader", supervisor: true);
        var otherLeader = User("dev-other-leader", supervisor: true);
        var admin = User("dev-approval-admin", supervisor: true, admin: true);

        // Case attachment: nothing formal before approval; worker commits exactly once.
        var caseTarget = new ThreeLevelUploadTarget(ThreeLevelUploadKind.CaseAttachment, caseId);
        var pendingResult = await upload.UploadAsync(caseTarget, [UploadFile(DemoFile("result-01.txt"))], engineer, "dev-approval-engineer", false);
        Assert.Equal(ApprovalUploadDecisionKind.Pending, pendingResult.Decision);
        var casePending = await LatestPendingAsync(factory, caseTarget, moduleCode);
        Assert.Equal(teamId, casePending.SubmittedTeamOptionId);
        await AssertNoFormalAsync(factory, casePending.Id);
        Assert.False(Directory.Exists(Path.Combine(formalRoot, tag)));
        Assert.Empty(Directory.GetFiles(rawRoot, DemoFile("result-01.txt"), SearchOption.AllDirectories));
        Assert.Contains(await submissions.ListForReviewAsync(leader, moduleCode), x => x.Id == casePending.Id);
        var activeSources = await submissions.ListActiveSourcesAsync();
        Assert.Contains(activeSources, x => x.Code == moduleCode);
        Assert.Contains(activeSources, x => x.Code == otherModuleCode);
        Assert.DoesNotContain(await submissions.ListForReviewAsync(leader, otherModuleCode),
            x => x.Id == casePending.Id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scope.DemandReviewAsync(otherLeader, casePending));
        await scope.DemandReviewAsync(admin, casePending);
        await submissions.ApproveAsync(casePending.Id, leader);
        Assert.Equal(ApprovalStatus.ApprovedPendingCommit, await StatusAsync(factory, casePending.Id));
        await AssertNoFormalAsync(factory, casePending.Id);
        await worker.ProcessAsync(casePending.Id);
        Assert.Equal(ApprovalStatus.Committed, await StatusAsync(factory, casePending.Id));
        await AssertFormalOnceAsync(factory, casePending, staging, rawRoot);
        await worker.ProcessAsync(casePending.Id);
        await handler.CommitAsync(await SubmissionAsync(factory, casePending.Id), default);
        await AssertFormalOnceAsync(factory, casePending, staging, rawRoot);

        // Rejection preserves reason and has no formal record or file.
        await upload.UploadAsync(caseTarget, [UploadFile(DemoFile("result-02.txt"))], engineer, "dev-approval-engineer", false);
        var rejected = await LatestPendingAsync(factory, caseTarget, moduleCode);
        Assert.NotEqual(casePending.Id, rejected.Id);
        await Assert.ThrowsAsync<ArgumentException>(() => submissions.RejectAsync(rejected.Id, leader, "  "));
        await submissions.RejectAsync(rejected.Id, leader, "DEV TEST correction required");
        Assert.Equal(ApprovalStatus.Rejected, await StatusAsync(factory, rejected.Id));
        await AssertNoFormalAsync(factory, rejected.Id);
        Assert.False(File.Exists(Path.Combine(formalRoot, tag, caseNo, DemoFile("result-02.txt"))));
        Assert.Contains(await submissions.ListRejectedMineAsync(engineer), x => x.Id == rejected.Id
            && x.RejectReason == "DEV TEST correction required");
        Assert.DoesNotContain(await submissions.ListRejectedMineAsync(User("other-engineer")), x => x.Id == rejected.Id);

        // Row locking makes a concurrent supervisor decision and submitter withdrawal choose one outcome.
        await upload.UploadAsync(caseTarget, [UploadFile(DemoFile("withdraw-race.txt"))],
            engineer, "dev-approval-engineer", false);
        var race = await LatestPendingAsync(factory, caseTarget, moduleCode);
        async Task<Exception?> Outcome(Func<Task> action)
        {
            try { await action(); return null; }
            catch (Exception ex) { return ex; }
        }
        var raceOutcomes = await Task.WhenAll(
            Outcome(() => submissions.WithdrawMineAsync(race.Id, engineer)),
            Outcome(() => submissions.ApproveAsync(race.Id, leader)));
        Assert.Single(raceOutcomes, x => x is null);
        var raceStatus = await StatusAsync(factory, race.Id);
        Assert.Contains(raceStatus, new[] { ApprovalStatus.Withdrawn, ApprovalStatus.ApprovedPendingCommit });
        await using (var db = await factory.CreateDbContextAsync())
        {
            var transitions = await db.ApprovalAuditEvents.CountAsync(x => x.SubmissionId == race.Id
                && (x.Action == ApprovalAuditActions.Withdrawn || x.Action == ApprovalAuditActions.Approved));
            Assert.Equal(1, transitions);
        }
        if (raceStatus == ApprovalStatus.Withdrawn)
        {
            await AssertNoFormalAsync(factory, race.Id);
            Assert.False(File.Exists(Path.Combine(stagingRoot, race.StagingStorageKey!.Replace('/', Path.DirectorySeparatorChar))));
        }
        else await worker.ProcessAsync(race.Id);

        // A missing staged file fails the worker; restoring it permits a safe retry.
        await upload.UploadAsync(caseTarget, [UploadFile(DemoFile("result-03.txt"))], engineer, "dev-approval-engineer", false);
        var retryPending = await LatestPendingAsync(factory, caseTarget, moduleCode);
        await submissions.ApproveAsync(retryPending.Id, leader);
        var stagedPath = Path.Combine(stagingRoot, retryPending.StagingStorageKey!.Replace('/', Path.DirectorySeparatorChar));
        var heldPath = stagedPath + ".hold";
        File.Move(stagedPath, heldPath);
        try
        {
            await worker.ProcessAsync(retryPending.Id);
            Assert.Equal(ApprovalStatus.CommitFailed, await StatusAsync(factory, retryPending.Id));
            await AssertNoFormalAsync(factory, retryPending.Id);
        }
        finally { File.Move(heldPath, stagedPath); }
        await submissions.RetryAsync(retryPending.Id, leader);
        await worker.ProcessAsync(retryPending.Id);
        Assert.Equal(ApprovalStatus.Committed, await StatusAsync(factory, retryPending.Id));
        await AssertFormalOnceAsync(factory, retryPending, staging, rawRoot);

        // v1 and a new version both become Approved and never enter the legacy pending list.
        var reportTarget = new ThreeLevelUploadTarget(ThreeLevelUploadKind.TestReport, taskId);
        await upload.UploadAsync(reportTarget, [UploadFile(DemoFile("report-01.pdf"))], engineer, "dev-approval-engineer", false);
        var report = await LatestPendingAsync(factory, reportTarget, moduleCode);
        var pendingInitialError = await Assert.ThrowsAsync<InvalidOperationException>(() => upload.UploadAsync(
            reportTarget, [UploadFile(DemoFile("duplicate-pending-root.pdf"))], engineer,
            "dev-approval-engineer", false));
        Assert.Equal(TestReportBusinessRules.PendingInitialMessage, pendingInitialError.Message);
        await AssertNoFormalAsync(factory, report.Id);
        Assert.IsType<NotFoundResult>(await kmDownload.Download(report.Id, 1, default));
        await using (var db = await factory.CreateDbContextAsync())
            Assert.False(await db.KmIntegrationOutbox.AnyAsync(x => x.SourceEntityId == report.Id));
        Assert.Empty(Directory.GetFiles(reportRoot, DemoFile("report-01.pdf"), SearchOption.AllDirectories));
        Assert.DoesNotContain(await formal.GetPendingTestReportsAsync(), x => x.Id == report.Id);
        Assert.Empty(Directory.GetFiles(rawRoot, DemoFile("report-01.pdf"), SearchOption.AllDirectories));
        await submissions.ApproveAsync(report.Id, leader);
        await worker.ProcessAsync(report.Id);
        Assert.Equal(ApprovalStatus.Committed, await StatusAsync(factory, report.Id));
        await AssertFormalOnceAsync(factory, report, staging, rawRoot);
        var downloaded = Assert.IsType<FileStreamResult>(await kmDownload.Download(report.Id, 1, default));
        Assert.Equal(DemoFile("report-01.pdf"), downloaded.FileDownloadName);
        await downloaded.FileStream.DisposeAsync();
        await using (var firstDb = await factory.CreateDbContextAsync())
        await using (var firstLock = await firstDb.Database.BeginTransactionAsync())
        await using (var secondDb = await factory.CreateDbContextAsync())
        await using (var secondLock = await secondDb.Database.BeginTransactionAsync())
        {
            var locked = await firstDb.KmIntegrationOutbox.FromSqlInterpolated(
                $"SELECT * FROM km_integration_outbox WHERE source_entity_id = {report.Id} FOR UPDATE")
                .SingleAsync();
            var skipped = await secondDb.KmIntegrationOutbox.FromSqlInterpolated(
                $"SELECT * FROM km_integration_outbox WHERE id = {locked.Id} FOR UPDATE SKIP LOCKED")
                .SingleOrDefaultAsync();
            Assert.Null(skipped);
        }
        Assert.IsType<NotFoundResult>(await kmDownload.Download(report.Id, 2, default));
        Assert.IsType<NotFoundResult>(await kmDownload.Download(Guid.NewGuid(), 1, default));
        var withdrawnTarget = new ThreeLevelUploadTarget(ThreeLevelUploadKind.TestReport, withdrawnTaskId);
        await upload.UploadAsync(withdrawnTarget, [UploadFile(DemoFile("withdrawn-report.pdf"))],
            engineer, "dev-approval-engineer", false);
        var withdrawnReport = await LatestPendingAsync(factory, withdrawnTarget, moduleCode);
        await submissions.WithdrawMineAsync(withdrawnReport.Id, engineer);
        Assert.Equal(ApprovalStatus.Withdrawn, await StatusAsync(factory, withdrawnReport.Id));
        await AssertNoFormalAsync(factory, withdrawnReport.Id);
        Assert.IsType<NotFoundResult>(await kmDownload.Download(withdrawnReport.Id, 1, default));
        await using (var db = await factory.CreateDbContextAsync())
            Assert.False(await db.KmIntegrationOutbox.AnyAsync(x => x.SourceEntityId == withdrawnReport.Id));
        await upload.UploadAsync(withdrawnTarget, [UploadFile(DemoFile("retry-report.pdf"))],
            engineer, "dev-approval-engineer", false);
        var failedReport = await LatestPendingAsync(factory, withdrawnTarget, moduleCode);
        await submissions.ApproveAsync(failedReport.Id, leader);
        var failedStage = Path.Combine(stagingRoot,
            failedReport.StagingStorageKey!.Replace('/', Path.DirectorySeparatorChar));
        File.Move(failedStage, failedStage + ".hold");
        try
        {
            await worker.ProcessAsync(failedReport.Id);
            Assert.Equal(ApprovalStatus.CommitFailed, await StatusAsync(factory, failedReport.Id));
            await AssertNoFormalAsync(factory, failedReport.Id);
            await using var db = await factory.CreateDbContextAsync();
            Assert.False(await db.KmIntegrationOutbox.AnyAsync(x => x.SourceEntityId == failedReport.Id));
        }
        finally { File.Move(failedStage + ".hold", failedStage); }
        await submissions.RetryAsync(failedReport.Id, leader);
        await worker.ProcessAsync(failedReport.Id);
        Assert.Equal(ApprovalStatus.Committed, await StatusAsync(factory, failedReport.Id));
        await using (var db = await factory.CreateDbContextAsync())
            Assert.Equal(1, await db.KmIntegrationOutbox.CountAsync(x => x.SourceEntityId == failedReport.Id));
        await using (var db = await factory.CreateDbContextAsync())
            Assert.Equal(1, await db.KmIntegrationOutbox.CountAsync(x => x.SourceEntityId == report.Id));
        var collisionDocumentId = Guid.NewGuid();
        async Task<Exception?> EnqueueSameVersionAsync()
        {
            await using var db = await factory.CreateDbContextAsync();
            var source = await db.ModuleCaseFiles.AsNoTracking().SingleAsync(x => x.Id == report.Id);
            source.Id = Guid.NewGuid();
            source.DocumentId = collisionDocumentId;
            db.KmIntegrationOutbox.Add(KmIntegrationOutbox.ForCommittedTestReport(source, DateTime.UtcNow));
            try { await db.SaveChangesAsync(); return null; }
            catch (Exception ex) { return ex; }
        }
        try
        {
            var enqueueResults = await Task.WhenAll(EnqueueSameVersionAsync(), EnqueueSameVersionAsync());
            Assert.Single(enqueueResults, x => x is null);
            Assert.Single(enqueueResults, x => x is DbUpdateException update &&
                update.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation });
            await using var db = await factory.CreateDbContextAsync();
            Assert.Equal(1, await db.KmIntegrationOutbox.CountAsync(x => x.DocumentId == collisionDocumentId));
        }
        finally
        {
            await using var db = await factory.CreateDbContextAsync();
            await db.KmIntegrationOutbox.Where(x => x.DocumentId == collisionDocumentId).ExecuteDeleteAsync();
        }
        var secondRoot = await Assert.ThrowsAsync<InvalidOperationException>(() => upload.UploadAsync(
            reportTarget, [UploadFile(DemoFile("duplicate-root.pdf"))], leader,
            "dev-approval-leader", true));
        Assert.Equal(TestReportBusinessRules.ExistingReportMessage, secondRoot.Message);
        var sameTaskByNo = new ThreeLevelUploadTarget(ThreeLevelUploadKind.TestReportByTaskNo, caseId, taskNo);
        var byNoSecondRoot = await Assert.ThrowsAsync<InvalidOperationException>(() => upload.UploadAsync(
            sameTaskByNo, [UploadFile(DemoFile("duplicate-by-no.pdf"))], leader,
            "dev-approval-leader", true));
        Assert.Equal(TestReportBusinessRules.ExistingReportMessage, byNoSecondRoot.Message);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var row = await db.ModuleCaseFiles.SingleAsync(x => x.Id == report.Id);
            Assert.Equal(1, row.VersionNo);
            Assert.True(row.IsFinal);
            Assert.Equal(TestReportReviewStatuses.Approved, row.ReviewStatus);
        }
        var versionTarget = new ThreeLevelUploadTarget(ThreeLevelUploadKind.TestReportVersion, report.Id);
        await upload.UploadAsync(versionTarget, [UploadFile(DemoFile("report-02.pdf"))], engineer, "dev-approval-engineer", false);
        var version = await LatestPendingAsync(factory, versionTarget, moduleCode);
        var pendingVersionError = await Assert.ThrowsAsync<InvalidOperationException>(() => upload.UploadAsync(
            versionTarget, [UploadFile(DemoFile("report-02b.pdf"))], engineer,
            "dev-approval-engineer", false));
        Assert.Equal(TestReportBusinessRules.PendingVersionMessage, pendingVersionError.Message);
        await AssertNoFormalAsync(factory, version.Id);
        Assert.Empty(Directory.GetFiles(reportRoot, DemoFile("report-02.pdf"), SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(rawRoot, DemoFile("report-02.pdf"), SearchOption.AllDirectories));
        await using (var db = await factory.CreateDbContextAsync())
            Assert.True(await db.ModuleCaseFiles.Where(x => x.Id == report.Id).Select(x => x.IsFinal).SingleAsync());
        await submissions.RejectAsync(version.Id, leader, "DEV TEST revise v2");
        Assert.IsType<NotFoundResult>(await kmDownload.Download(report.Id, 2, default));
        var rejectedVersionId = version.Id;
        Assert.Equal(ApprovalStatus.Rejected, await StatusAsync(factory, version.Id));
        await AssertNoFormalAsync(factory, version.Id);
        await using (var db = await factory.CreateDbContextAsync())
            Assert.False(await db.KmIntegrationOutbox.AnyAsync(x => x.SourceEntityId == version.Id));
        Assert.Contains(await submissions.ListRejectedMineAsync(engineer), x => x.Id == version.Id
            && x.RejectReason == "DEV TEST revise v2" && x.HasStagingFile);
        Assert.DoesNotContain(await submissions.ListRejectedMineAsync(User("other-engineer")),
            x => x.Id == version.Id);
        var rejectedFile = await submissions.OpenFileAsync(version.Id, engineer);
        await using (rejectedFile.Stream)
            Assert.True(rejectedFile.Stream.Length > 0);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
        {
            var forbidden = await submissions.OpenFileAsync(version.Id, User("other-engineer"));
            await forbidden.Stream.DisposeAsync();
        });
        await using (var db = await factory.CreateDbContextAsync())
            Assert.True(await db.ApprovalAuditEvents.AnyAsync(x => x.SubmissionId == version.Id
                && x.Action == ApprovalAuditActions.Rejected));
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.True(await db.ModuleCaseFiles.Where(x => x.Id == report.Id).Select(x => x.IsFinal).SingleAsync());
            Assert.Equal(1, await db.ModuleCaseFiles.CountAsync(x => x.DocumentId == report.Id));
        }
        Assert.Empty(Directory.GetFiles(reportRoot, DemoFile("report-02.pdf"), SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(rawRoot, DemoFile("report-02.pdf"), SearchOption.AllDirectories));
        var revisedVersionId = await upload.ResubmitAsync(versionTarget,
            UploadFile(DemoFile("report-03.pdf")), engineer, rejectedVersionId);
        Assert.NotEqual(rejectedVersionId, revisedVersionId);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var oldVersion = await db.PendingSubmissions.SingleAsync(x => x.Id == rejectedVersionId);
            Assert.Equal(ApprovalStatus.Rejected, oldVersion.Status);
            Assert.Equal(revisedVersionId, oldVersion.ResubmittedAsSubmissionId);
            Assert.True(await db.ApprovalAuditEvents.AnyAsync(x => x.SubmissionId == rejectedVersionId
                && x.Action == ApprovalAuditActions.Resubmitted));
        }
        Assert.DoesNotContain(await submissions.ListRejectedMineAsync(engineer), x => x.Id == rejectedVersionId);
        version = await LatestPendingAsync(factory, versionTarget, moduleCode);
        Assert.Equal(revisedVersionId, version.Id);
        await submissions.ApproveAsync(version.Id, leader);
        // Force a business-key collision with another formal file. Formal version 2,
        // the Committed transition, and the outbox must all roll back together.
        await using (var db = await factory.CreateDbContextAsync())
            Assert.Equal(1, await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE km_integration_outbox SET version = 2 WHERE source_entity_id = {report.Id}"));
        try
        {
            await worker.ProcessAsync(version.Id);
            Assert.Equal(ApprovalStatus.CommitFailed, await StatusAsync(factory, version.Id));
            await AssertNoFormalAsync(factory, version.Id);
            await using var db = await factory.CreateDbContextAsync();
            Assert.False(await db.KmIntegrationOutbox.AnyAsync(x => x.SourceEntityId == version.Id));
        }
        finally
        {
            await using var db = await factory.CreateDbContextAsync();
            Assert.Equal(1, await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE km_integration_outbox SET version = 1 WHERE source_entity_id = {report.Id}"));
        }
        await submissions.RetryAsync(version.Id, leader);
        await worker.ProcessAsync(version.Id);
        Assert.Equal(ApprovalStatus.Committed, await StatusAsync(factory, version.Id));
        await AssertFormalOnceAsync(factory, version, staging, rawRoot);
        await using (var db = await factory.CreateDbContextAsync())
            Assert.Equal(1, await db.KmIntegrationOutbox.CountAsync(x => x.SourceEntityId == version.Id));
        await using (var db = await factory.CreateDbContextAsync())
        {
            var rows = await db.ModuleCaseFiles.Where(x => x.DocumentId == report.Id).OrderBy(x => x.VersionNo).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Equal(2, rows[1].VersionNo);
            Assert.True(rows[1].IsFinal);
            Assert.False(rows[0].IsFinal);
            Assert.All(rows, x => Assert.Equal(TestReportReviewStatuses.Approved, x.ReviewStatus));
        }
        Assert.DoesNotContain(await formal.GetPendingTestReportsAsync(), x => x.Id == report.Id || x.Id == version.Id);

        // A pending version blocks another submission even when it targets a different file in the same root.
        var latestVersionTarget = new ThreeLevelUploadTarget(ThreeLevelUploadKind.TestReportVersion, version.Id);
        await upload.UploadAsync(latestVersionTarget, [UploadFile(DemoFile("report-pending-03.pdf"))],
            engineer, "dev-approval-engineer", false);
        var pendingLaterVersion = await LatestPendingAsync(factory, latestVersionTarget, moduleCode);
        var crossFileVersionError = await Assert.ThrowsAsync<InvalidOperationException>(() => upload.UploadAsync(
            versionTarget, [UploadFile(DemoFile("report-pending-04.pdf"))], engineer,
            "dev-approval-engineer", false));
        Assert.Equal(TestReportBusinessRules.PendingVersionMessage, crossFileVersionError.Message);
        await submissions.RejectAsync(pendingLaterVersion.Id, leader, "DEV TEST retry permitted");

        // Supervisor report uploads and versions go straight to an approved Final.
        var beforeReportBypass = await CountPendingAsync(factory);
        var bypassReportTarget = new ThreeLevelUploadTarget(ThreeLevelUploadKind.TestReport, bypassTaskId);
        var bypassReport = await upload.UploadAsync(bypassReportTarget,
            [UploadFile(DemoFile("report-04.pdf"))], leader, "dev-approval-leader", true);
        Assert.Equal(ApprovalUploadDecisionKind.ApprovalBypassed, bypassReport.Decision);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var row = await db.ModuleCaseFiles.SingleAsync(x => x.FileName == supervisorReportName);
            Assert.Equal(TestReportReviewStatuses.Approved, row.ReviewStatus);
            Assert.True(row.IsFinal);
            Assert.True(row.IsRawDataExported);
            Assert.Equal(bypassTaskId, row.TaskId);
            Assert.Equal(1, await db.KmIntegrationOutbox.CountAsync(x => x.SourceEntityId == row.Id));
            var bypassVersion = await upload.UploadAsync(
                new ThreeLevelUploadTarget(ThreeLevelUploadKind.TestReportVersion, row.Id),
                [UploadFile(DemoFile("report-05.pdf"))], leader, "dev-approval-leader", true);
            Assert.Equal(ApprovalUploadDecisionKind.ApprovalBypassed, bypassVersion.Decision);
        }
        Assert.Equal(beforeReportBypass, await CountPendingAsync(factory));
        await using (var db = await factory.CreateDbContextAsync())
        {
            var rows = await db.ModuleCaseFiles.Where(x => x.FileName == supervisorReportName
                || x.FileName == supervisorReportV2Name).OrderBy(x => x.VersionNo).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Equal(2, rows[1].VersionNo);
            Assert.True(rows[1].IsFinal);
            Assert.False(rows[0].IsFinal);
            Assert.All(rows, x => Assert.Equal(TestReportReviewStatuses.Approved, x.ReviewStatus));
            Assert.All(rows, row => Assert.Equal(1,
                db.KmIntegrationOutbox.Count(x => x.SourceEntityId == row.Id)));
        }

        // A legacy formal Pending report stays in the old review query; a generic Pending
        // remains exclusively in the supervisor center query.
        await formal.UploadNewTestReportVersionAsync(report.Id, UploadFile(DemoFile("legacy-01.pdf")),
            "dev-approval-engineer");
        var legacyPending = Assert.Single(await formal.GetPendingTestReportsAsync(),
            x => x.FileName == DemoFile("legacy-01.pdf"));
        await upload.UploadAsync(caseTarget, [UploadFile(DemoFile("result-04.txt"))],
            engineer, "dev-approval-engineer", false);
        var genericPending = await LatestPendingAsync(factory, caseTarget, moduleCode);
        Assert.Contains(await submissions.ListForReviewAsync(leader, moduleCode), x => x.Id == genericPending.Id);
        Assert.DoesNotContain(await submissions.ListForReviewAsync(leader, moduleCode), x => x.Id == legacyPending.Id);
        Assert.DoesNotContain(await formal.GetPendingTestReportsAsync(), x => x.Id == genericPending.Id);
        await formal.ReviewTestReportAsync(legacyPending.Id,
            new SIT.DepartmentSystem.Web.Models.Api.TestReportReviewRequest { Decision = "Approved" },
            "dev-approval-leader");
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.True(await db.ModuleCaseFiles.Where(x => x.Id == legacyPending.Id).Select(x => x.IsFinal).SingleAsync());
            Assert.Equal(1, await db.KmIntegrationOutbox.CountAsync(x => x.SourceEntityId == legacyPending.Id));
        }
        await formal.UploadNewTestReportVersionAsync(report.Id, UploadFile(DemoFile("legacy-02.pdf")),
            "dev-approval-engineer");
        var legacyRejected = Assert.Single(await formal.GetPendingTestReportsAsync(),
            x => x.FileName == DemoFile("legacy-02.pdf"));
        await formal.ReviewTestReportAsync(legacyRejected.Id,
            new SIT.DepartmentSystem.Web.Models.Api.TestReportReviewRequest { Decision = "Rejected",
                Comment = "DEV TEST legacy correction" }, "dev-approval-leader");
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.True(await db.ModuleCaseFiles.Where(x => x.Id == legacyPending.Id).Select(x => x.IsFinal).SingleAsync());
            Assert.Equal(TestReportReviewStatuses.Rejected,
                await db.ModuleCaseFiles.Where(x => x.Id == legacyRejected.Id).Select(x => x.ReviewStatus).SingleAsync());
            Assert.False(await db.KmIntegrationOutbox.AnyAsync(x => x.SourceEntityId == legacyRejected.Id));
        }
        var formalVersions = (await formal.GetTaskFilesAsync(taskId))
            .Where(x => x.IsCommittedFormalVersion).OrderByDescending(x => x.VersionNo).ToList();
        Assert.Equal(new[] { 3, 2, 1 }, formalVersions.Select(x => x.VersionNo));
        Assert.DoesNotContain(formalVersions, x => x.Id == legacyRejected.Id);
        Assert.DoesNotContain(await formal.GetTaskFilesAsync(taskId), x => x.Id == rejectedVersionId);

        // Administration upload bypasses Pending and records its audit event.
        var beforeBypass = await CountPendingAsync(factory);
        var bypass = await upload.UploadAsync(caseTarget, [UploadFile(DemoFile("result-05.txt"))], admin,
            "dev-approval-admin", true);
        Assert.Equal(ApprovalUploadDecisionKind.ApprovalBypassed, bypass.Decision);
        Assert.Equal(beforeBypass, await CountPendingAsync(factory));
        await using (var db = await factory.CreateDbContextAsync())
            Assert.True(await db.ApprovalAuditEvents.AnyAsync(x => x.Action == ApprovalAuditActions.ApprovalBypassed
                && x.ActorAccount == "dev-approval-admin"));

        // Every remaining shared ThreeLevel entry uses the same pending/worker path.
        var additional = new[]
        {
            new ThreeLevelUploadTarget(ThreeLevelUploadKind.CaseAttachmentByNo, recordId, caseNo),
            new ThreeLevelUploadTarget(ThreeLevelUploadKind.TaskAttachment, taskId),
            new ThreeLevelUploadTarget(ThreeLevelUploadKind.TaskAttachmentByNo, caseId, taskNo),
            new ThreeLevelUploadTarget(ThreeLevelUploadKind.TestReportByTaskNo, caseId, byNoTaskNo)
        };
        for (var i = 0; i < additional.Length; i++)
        {
            var target = additional[i];
            var filename = DemoFile($"entry-{i + 1:00}" +
                (target.Kind == ThreeLevelUploadKind.TestReportByTaskNo ? ".pdf" : ".txt"));
            var result = await upload.UploadAsync(target, [UploadFile(filename)], engineer,
                "dev-approval-engineer", false);
            Assert.Equal(ApprovalUploadDecisionKind.Pending, result.Decision);
            var pending = await LatestPendingAsync(factory, target, moduleCode);
            await AssertNoFormalAsync(factory, pending.Id);
            await submissions.ApproveAsync(pending.Id, leader);
            await worker.ProcessAsync(pending.Id);
            Assert.Equal(ApprovalStatus.Committed, await StatusAsync(factory, pending.Id));
            await AssertFormalOnceAsync(factory, pending, staging, rawRoot);
        }

        // Two independent request scopes must not create two roots for one TaskNo.
        await using (var firstDb = await factory.CreateDbContextAsync())
        await using (var secondDb = await factory.CreateDbContextAsync())
        {
            CaseFileService Service(AppDbContext db) => new(db,
                Options.Create(new UploadSettings { BasePath = formalRoot, UploadKind = "Lab" }),
                rawSettings, new RawDataExportService(rawSettings), NullLogger<CaseFileService>.Instance);
            async Task<Exception?> Attempt(CaseFileService service, string filename)
            {
                try { await service.UploadTaskReportByTaskNoAsync(caseId, concurrentTaskNo,
                    [UploadFile(DemoFile(filename))], "dev-approval-leader", autoApprove: true); return null; }
                catch (Exception ex) { return ex; }
            }
            var outcomes = await Task.WhenAll(
                Attempt(Service(firstDb), "parallel-01.pdf"),
                Attempt(Service(secondDb), "parallel-02.pdf"));
            Assert.Single(outcomes, x => x is null);
            Assert.Single(outcomes, x => x is InvalidOperationException error
                && error.Message == TestReportBusinessRules.ExistingReportMessage);
        }
        await using (var db = await factory.CreateDbContextAsync())
            Assert.Equal(1, await db.ModuleCaseFiles.Where(x => x.CaseId == caseId
                && x.TaskNo == concurrentTaskNo && x.FileKind == ModuleCaseFileKinds.TestReport)
                .Select(x => x.DocumentId).Distinct().CountAsync());
        await fileDb.DisposeAsync();
    }

    private static async Task<PendingSubmission> LatestPendingAsync(TestDbFactory factory,
        ThreeLevelUploadTarget target, string moduleCode)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.PendingSubmissions.AsNoTracking()
            .Where(x => x.SourceModuleCode == moduleCode &&
                x.ContextType == target.ContextType && x.ContextId == target.ContextId)
            .OrderByDescending(x => x.SubmittedAt).FirstAsync();
    }

    private static async Task<ApprovalStatus> StatusAsync(TestDbFactory factory, Guid id)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.PendingSubmissions.Where(x => x.Id == id).Select(x => x.Status).SingleAsync();
    }

    private static async Task<PendingSubmission> SubmissionAsync(TestDbFactory factory, Guid id)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.PendingSubmissions.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    private static async Task<int> CountPendingAsync(TestDbFactory factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.PendingSubmissions.CountAsync();
    }

    private static async Task AssertNoFormalAsync(TestDbFactory factory, Guid id)
    {
        await using var db = await factory.CreateDbContextAsync();
        Assert.False(await db.ModuleCaseFiles.AnyAsync(x => x.Id == id));
    }

    private static async Task AssertFormalOnceAsync(TestDbFactory factory, PendingSubmission pending,
        IApprovalStagingStorage staging, string rawRoot)
    {
        await using var db = await factory.CreateDbContextAsync();
        var file = await db.ModuleCaseFiles.SingleAsync(x => x.Id == pending.Id);
        Assert.True(file.IsRawDataExported);
        Assert.True(File.Exists(Path.Combine(file.FilePath, file.FileName)));
        Assert.NotNull(file.NasFilePath);
        Assert.True(File.Exists(file.NasFilePath));
        Assert.StartsWith(Path.GetFullPath(@"D:\CSIT_Dev_Data"), Path.GetFullPath(file.FilePath),
            StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Path.GetFullPath(rawRoot), Path.GetFullPath(file.NasFilePath),
            StringComparison.OrdinalIgnoreCase);
        await using var stage = await staging.OpenReadAsync(pending.Id, pending.StagingStorageKey!);
        await using var formal = File.OpenRead(Path.Combine(file.FilePath, file.FileName));
        await using var mirrored = File.OpenRead(file.NasFilePath);
        Assert.Equal(Convert.ToHexString(await SHA256.HashDataAsync(stage)),
            Convert.ToHexString(await SHA256.HashDataAsync(formal)));
        Assert.Equal(pending.FileHash, Convert.ToHexString(await SHA256.HashDataAsync(mirrored)),
            ignoreCase: true);
        Assert.True(Directory.Exists(rawRoot));
    }

    private static IFormFile UploadFile(string name)
    {
        byte[] bytes = [1, 2, 3, 4, 5, 6];
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "files", name)
        { Headers = new HeaderDictionary(), ContentType = "application/octet-stream" };
    }

    private static ClaimsPrincipal User(string account, bool supervisor = false, bool admin = false)
    {
        var claims = new List<Claim> { new("account", account), new("can_write", "true") };
        if (supervisor) claims.Add(new("supervisor", "true"));
        if (admin) claims.Add(new(ClaimTypes.Role, "Admin"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private sealed class TestDbFactory(string connection) : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connection).Options;
        public AppDbContext CreateDbContext() => new(_options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class TestRoot(string root) : IApprovalStagingRootResolver
    {
        public Task<string> GetRootAsync(CancellationToken cancellationToken = default) => Task.FromResult(root);
    }

    private sealed class TestAuthorization : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource,
            IEnumerable<IAuthorizationRequirement> requirements) => Task.FromResult(AuthorizationResult.Failed());
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) =>
            Task.FromResult(policyName == SystemAuthorization.Policies.BusinessWrite && user.HasClaim("can_write", "true")
                || policyName == SystemAuthorization.Policies.Administration && user.HasClaim("supervisor", "true")
                ? AuthorizationResult.Success() : AuthorizationResult.Failed());
    }
}
