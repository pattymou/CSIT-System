using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Config;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.KmIntegration;
using SIT.DepartmentSystem.Web.Services.Approval;

namespace ApprovalFramework.Tests;

public sealed class VerificationCaseFreezeAndCleanupTests
{
    [Fact]
    public async Task DisabledDeletionBlocksFreezeAndBothLocalCleanupEntryPoints()
    {
        using var directory = new TestDirectory();
        var factory = new Factory();
        var (application, record) = Accepted("VA-DISABLED", "Model A");
        await using (var seed = factory.CreateDbContext())
        {
            seed.VerificationApplications.Add(application);
            seed.ModuleRecords.Add(record);
            await seed.SaveChangesAsync();
        }
        await using (var db = factory.CreateDbContext())
        {
            var confirmation = new VerificationCaseDeletionConfirmationService(db,
                new VerificationCaseDeletionPreparationService(db, new AllowWrite()),
                Archive(factory, directory.Path), Options.Create(new KmDeletionOptions()));
            await Assert.ThrowsAsync<InvalidOperationException>(() => confirmation.ConfirmAsync(
                record.Id, application.ApplicationNo, "Delete this accepted case", Manager()));
            Assert.Empty(await db.VerificationCaseDeletionJobs.ToListAsync());
        }

        var job = VerificationCaseDeletionJob.Prepare(application.Id, record.Id,
            "leader", "Synthetic disabled cleanup check", DateTime.UtcNow);
        job.MarkLocalReady(DateTime.UtcNow);
        await using (var seed = factory.CreateDbContext())
        {
            seed.VerificationCaseDeletionJobs.Add(job);
            await seed.SaveChangesAsync();
        }
        await using (var db = factory.CreateDbContext())
        {
            var cleanup = Cleanup(db, factory, directory.Path, deletionEnabled: false);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                cleanup.BeginAsync(record.Id, Admin()));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                cleanup.ProcessAsync(record.Id, Admin()));
            Assert.Equal("LocalReady",
                (await db.VerificationCaseDeletionJobs.SingleAsync()).Status);
            Assert.True(await db.VerificationApplications.AnyAsync(x => x.Id == application.Id));
            Assert.True(await db.ModuleRecords.AnyAsync(x => x.Id == record.Id));
        }
    }

    [Fact]
    public async Task ConfirmationFreezesExactAcceptedCaseAndRejectsLaterWrites()
    {
        using var directory = new TestDirectory();
        var factory = new Factory();
        var (application, record) = Accepted("VA-FREEZE", "Same Model");
        var (_, other) = Accepted("VA-OTHER", "Same Model");
        await using (var seed = factory.CreateDbContext())
        {
            seed.VerificationApplications.Add(application);
            seed.ModuleRecords.AddRange(record, other);
            await seed.SaveChangesAsync();
        }
        await using (var db = factory.CreateDbContext())
        {
            var archive = Archive(factory, directory.Path);
            var confirmation = new VerificationCaseDeletionConfirmationService(db,
                new VerificationCaseDeletionPreparationService(db, new AllowWrite()), archive,
                Options.Create(new KmDeletionOptions { Enabled = true }));
            await Assert.ThrowsAsync<InvalidOperationException>(() => confirmation.ConfirmAsync(
                record.Id, "VA-WRONG", "Delete this accepted case", Manager()));
            Assert.Equal("Prepared", (await db.VerificationCaseDeletionJobs.SingleAsync()).Status);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => confirmation.ConfirmAsync(
                record.Id, application.ApplicationNo, "Delete this accepted case", Manager("staff")));
            var job = await confirmation.ConfirmAsync(record.Id, application.ApplicationNo,
                "Delete this accepted case", Manager());
            Assert.Equal("LocalReady", job.Status);
            Assert.Contains(await db.ApprovalAuditEvents.ToListAsync(), x =>
                x.SubmissionId == job.Id && x.Action == ApprovalAuditActions.CaseDeletionFrozen &&
                x.ActorAccount == "leader");
        }
        await using (var writer = factory.CreateDbContext())
        {
            var frozen = await writer.ModuleRecords.SingleAsync(x => x.Id == record.Id);
            frozen.Status = "Hold";
            await Assert.ThrowsAsync<InvalidOperationException>(() => writer.SaveChangesAsync());
            writer.ChangeTracker.Clear();
            writer.ModuleCaseFiles.Add(new ModuleCaseFile
            {
                Id = Guid.NewGuid(), RecordId = record.Id, FileName = "report.pdf",
                FilePath = directory.Path, DocumentId = Guid.NewGuid(), VersionNo = 1
            });
            await Assert.ThrowsAsync<InvalidOperationException>(() => writer.SaveChangesAsync());
            writer.ChangeTracker.Clear();
            var unaffected = await writer.ModuleRecords.SingleAsync(x => x.Id == other.Id);
            unaffected.Status = "Hold";
            await writer.SaveChangesAsync();
            Assert.Equal("Hold", (await writer.ModuleRecords.SingleAsync(x => x.Id == other.Id)).Status);
        }
    }

    [Fact]
    public async Task KmAcceptedIsNotLocalReadyAndOnlyVerifiedCallbackUnlocksCleanup()
    {
        var now = DateTime.UtcNow;
        var job = VerificationCaseDeletionJob.Prepare(Guid.NewGuid(), Guid.NewGuid(),
            "leader", "Delete this accepted case", now);
        job.AddTarget(new KmDeletionResource("TestReport", null, Guid.NewGuid(), 1,
            Guid.NewGuid(), job.ApplicationId, job.RecordId), now);
        job.AddTarget(new KmDeletionResource("TestReport", null, Guid.NewGuid(), 2,
            Guid.NewGuid(), job.ApplicationId, job.RecordId), now);
        var local = job.Targets[0];
        local.MarkNotDispatched(now);
        job.MarkWaitingForKm(now);
        Assert.Throws<InvalidOperationException>(() => job.MarkLocalCleanup("[]", now));
        var remote = job.Targets[1];
        var lease = remote.ClaimDispatch(now, TimeSpan.FromMinutes(2));
        remote.DeliveryAccepted(lease, "KM-JOB-1", now);
        Assert.Throws<InvalidOperationException>(() => job.MarkKmVerified(now));
        var callback = new KmDeletionCallback("1.0", Guid.NewGuid(), "CSIT", remote.Id,
            "KM-JOB-1", remote.Resource, "Completed",
            new KmDeletionTimestamps(new DateTimeOffset(now), new DateTimeOffset(now.AddSeconds(2)), null),
            new KmDeletionResult(true, new DateTimeOffset(now.AddSeconds(1)),
                new KmDeletionCleanup("Deleted", "Deleted", "Deleted", "Deleted",
                    "Deleted", "Deleted", "NotPresent")), null);
        remote.ApplyCallback(callback, now.AddSeconds(2));
        job.MarkKmVerified(now.AddSeconds(2));
        Assert.Equal("KmVerified", job.Status);
    }

    [Fact]
    public async Task FileFailureKeepsDurableCleanupAndRetryLeavesSameNameOtherCaseUntouched()
    {
        using var directory = new TestDirectory();
        var firstPath = System.IO.Path.Combine(directory.Path, "first.txt");
        var secondPath = System.IO.Path.Combine(directory.Path, "second.txt");
        await File.WriteAllTextAsync(firstPath, "first case");
        await File.WriteAllTextAsync(secondPath, "second case");
        var file = await VerificationCaseDeletionPhysicalFiles.InspectAsync(
            "ModuleCaseFile", Guid.NewGuid(), directory.Path, firstPath);
        var factory = new Factory();
        var (application, record) = Accepted("VA-FIRST", "Same Model");
        var (otherApplication, otherRecord) = Accepted("VA-SECOND", "Same Model");
        var job = VerificationCaseDeletionJob.Prepare(application.Id, record.Id,
            "leader", "Delete this accepted case", DateTime.UtcNow);
        job.MarkLocalReady(DateTime.UtcNow);
        job.MarkLocalCleanup(JsonSerializer.Serialize(new[] { file },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)), DateTime.UtcNow);
        await using (var seed = factory.CreateDbContext())
        {
            seed.VerificationApplications.AddRange(application, otherApplication);
            seed.ModuleRecords.AddRange(record, otherRecord);
            seed.Modules.AddRange(
                new ModuleEntity { Id = record.ModuleId, Code = "verification", Name = "Verification" },
                new ModuleEntity { Id = otherRecord.ModuleId, Code = "other", Name = "Other" });
            seed.VerificationCaseDeletionJobs.Add(job);
            await seed.SaveChangesAsync();
        }
        await File.WriteAllTextAsync(firstPath, "changed after freeze");
        await using (var db = factory.CreateDbContext())
        {
            var cleanup = Cleanup(db, factory, directory.Path);
            await Assert.ThrowsAsync<InvalidOperationException>(() => cleanup.ProcessAsync(record.Id, Admin()));
            Assert.Equal("LocalCleanup", (await db.VerificationCaseDeletionJobs.SingleAsync()).Status);
            Assert.Null((await db.VerificationCaseDeletionJobs.SingleAsync()).DbCleanedAt);
            Assert.Equal("FILE_PREFLIGHT_FAILED",
                (await db.VerificationCaseDeletionJobs.SingleAsync()).LastCleanupErrorCode);
            Assert.Contains(await db.ApprovalAuditEvents.ToListAsync(), x =>
                x.Action == ApprovalAuditActions.CaseDeletionCleanupFailed);
            Assert.True(await db.VerificationApplications.AnyAsync(x => x.Id == application.Id));
            Assert.True(await db.VerificationApplications.AnyAsync(x => x.Id == otherApplication.Id));
        }
        await File.WriteAllTextAsync(firstPath, "first case");
        await using (var retry = factory.CreateDbContext())
        {
            var completed = await Cleanup(retry, factory, directory.Path)
                .ProcessAsync(record.Id, Admin());
            Assert.Equal("Completed", completed.Status);
            Assert.NotNull(completed.CompletedAt);
            Assert.Contains(await retry.ApprovalAuditEvents.ToListAsync(), x =>
                x.Action == ApprovalAuditActions.CaseDeletionCompleted && x.ActorAccount == "admin");
            Assert.False(File.Exists(firstPath));
            Assert.True(File.Exists(secondPath));
            Assert.True(await retry.ModuleRecords.AnyAsync(x => x.Id == otherRecord.Id));
        }
    }

    [Fact]
    public async Task FileSafetyRejectsEscapeAndReplacedContent()
    {
        using var directory = new TestDirectory();
        var path = System.IO.Path.Combine(directory.Path, "owned.txt");
        await File.WriteAllTextAsync(path, "owned");
        var entry = await VerificationCaseDeletionPhysicalFiles.InspectAsync(
            "TestReport", Guid.NewGuid(), directory.Path, path);
        Assert.Throws<InvalidOperationException>(() => VerificationCaseDeletionPhysicalFiles.Validate(
            directory.Path, System.IO.Path.GetTempPath()));
        await File.WriteAllTextAsync(path, "replaced");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            VerificationCaseDeletionPhysicalFiles.DeleteAsync(entry));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task RestartAfterDatabaseCleanupFinishesFromDurableManifest()
    {
        using var directory = new TestDirectory();
        var path = System.IO.Path.Combine(directory.Path, "remaining.pdf");
        await File.WriteAllTextAsync(path, "remaining case file");
        var entry = await VerificationCaseDeletionPhysicalFiles.InspectAsync(
            "ModuleCaseFile", Guid.NewGuid(), directory.Path, path);
        var factory = new Factory();
        var job = VerificationCaseDeletionJob.Prepare(Guid.NewGuid(), Guid.NewGuid(),
            "leader", "Delete this accepted case", DateTime.UtcNow);
        job.MarkLocalReady(DateTime.UtcNow);
        job.MarkLocalCleanup(JsonSerializer.Serialize(new[] { entry },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)), DateTime.UtcNow);
        job.MarkDbCleaned(DateTime.UtcNow);
        await using (var seed = factory.CreateDbContext())
        {
            seed.VerificationCaseDeletionJobs.Add(job);
            await seed.SaveChangesAsync();
        }
        await using (var resumed = factory.CreateDbContext())
        {
            var completed = await Cleanup(resumed, factory, directory.Path)
                .ProcessAsync(job.RecordId, Admin());
            Assert.Equal("Completed", completed.Status);
            Assert.False(File.Exists(path));
            Assert.Contains(await resumed.ApprovalAuditEvents.ToListAsync(), x =>
                x.Action == ApprovalAuditActions.CaseDeletionCompleted);
        }
    }

    [Fact]
    public async Task ConfirmationRefusesDispatchingAndLeavesCaseWritable()
    {
        using var directory = new TestDirectory();
        var factory = new Factory();
        var (application, record) = Accepted("VA-DISPATCH", "Model A");
        var report = new ModuleCaseFile
        {
            Id = Guid.NewGuid(), RecordId = record.Id, FileName = "report.pdf",
            FilePath = directory.Path, FileSize = 10, DocumentId = Guid.NewGuid(),
            VersionNo = 1, FileKind = ModuleCaseFileKinds.TestReport,
            ReviewStatus = TestReportReviewStatuses.Approved, IsRawDataExported = true
        };
        var outbox = KmIntegrationOutbox.ForCommittedTestReport(report, DateTime.UtcNow);
        outbox.Claim(Guid.NewGuid(), DateTime.UtcNow, TimeSpan.FromMinutes(2));
        await using (var seed = factory.CreateDbContext())
        {
            seed.VerificationApplications.Add(application);
            seed.ModuleRecords.Add(record);
            seed.ModuleCaseFiles.Add(report);
            seed.KmIntegrationOutbox.Add(outbox);
            await seed.SaveChangesAsync();
        }
        await using var db = factory.CreateDbContext();
        var service = new VerificationCaseDeletionConfirmationService(db,
            new VerificationCaseDeletionPreparationService(db, new AllowWrite()),
            Archive(factory, directory.Path), Options.Create(new KmDeletionOptions { Enabled = true }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfirmAsync(record.Id,
            application.ApplicationNo, "Delete this accepted case", Manager()));
        Assert.Equal("Prepared", (await db.VerificationCaseDeletionJobs.SingleAsync()).Status);
        db.ChangeTracker.Clear();
        var writable = await db.ModuleRecords.SingleAsync(x => x.Id == record.Id);
        writable.Status = "Hold";
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ConfirmationRefusesPendingApprovalAndArchiveWork()
    {
        using var directory = new TestDirectory();
        var factory = new Factory();
        var (application, record) = Accepted("VA-PENDING", "Model A");
        var recordCase = new ModuleRecordCase { Id = Guid.NewGuid(), RecordId = record.Id,
            CaseNo = "RADIO", Name = "Radio" };
        var pending = PendingSubmission.Create(Guid.NewGuid(), "test-submission", "verification",
            "verification", "applicant", "RD", application.TeamOptionId!.Value, "TEAM-A",
            nameof(ThreeLevelUploadKind.CaseAttachment),
            new ThreeLevelUploadTarget(ThreeLevelUploadKind.CaseAttachment, recordCase.Id).ContextId,
            "Case attachment", null, DateTime.UtcNow);
        await using (var seed = factory.CreateDbContext())
        {
            seed.VerificationApplications.Add(application);
            seed.ModuleRecords.Add(record);
            seed.ModuleRecordCases.Add(recordCase);
            seed.PendingSubmissions.Add(pending);
            await seed.SaveChangesAsync();
        }
        await using (var db = factory.CreateDbContext())
        {
            var service = new VerificationCaseDeletionConfirmationService(db,
                new VerificationCaseDeletionPreparationService(db, new AllowWrite()),
                Archive(factory, directory.Path), Options.Create(new KmDeletionOptions { Enabled = true }));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfirmAsync(record.Id,
                application.ApplicationNo, "Delete this accepted case", Manager()));
            Assert.Equal("Prepared", (await db.VerificationCaseDeletionJobs.SingleAsync()).Status);
        }
        await using (var db = factory.CreateDbContext())
        {
            db.PendingSubmissions.Remove(await db.PendingSubmissions.SingleAsync());
            await db.SaveChangesAsync();
        }
        var archive = Archive(factory, directory.Path);
        await archive.EnqueueApprovedApplicationAsync(application, DateTime.UtcNow, default);
        // There is no attachment in this fixture; enqueue a synthetic exact attachment ID.
        var journal = new VerificationAttachmentArchiveJournal(
            Options.Create(new DocumentStagingOptions { RootPath = directory.Path }));
        await journal.EnqueueAsync(application.Id, [Guid.NewGuid()], DateTime.UtcNow, default);
        await using (var db = factory.CreateDbContext())
        {
            var service = new VerificationCaseDeletionConfirmationService(db,
                new VerificationCaseDeletionPreparationService(db, new AllowWrite()),
                Archive(factory, directory.Path), Options.Create(new KmDeletionOptions { Enabled = true }));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfirmAsync(record.Id,
                application.ApplicationNo, "Delete this accepted case", Manager()));
            Assert.Equal("Prepared", (await db.VerificationCaseDeletionJobs.SingleAsync()).Status);
        }
    }

    [Fact]
    public async Task LocalCleanupRequiresAdministratorAndBusinessWriteForBothEntryPoints()
    {
        using var directory = new TestDirectory();
        var factory = new Factory();
        var job = VerificationCaseDeletionJob.Prepare(Guid.NewGuid(), Guid.NewGuid(),
            "leader", "Delete this accepted case", DateTime.UtcNow);
        job.MarkLocalReady(DateTime.UtcNow);
        await using (var seed = factory.CreateDbContext())
        {
            seed.VerificationCaseDeletionJobs.Add(job);
            await seed.SaveChangesAsync();
        }
        await using (var db = factory.CreateDbContext())
        {
            var cleanup = Cleanup(db, factory, directory.Path);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                cleanup.BeginAsync(job.RecordId, Manager()));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                cleanup.ProcessAsync(job.RecordId, Manager()));
            Assert.Equal("LocalReady",
                (await db.VerificationCaseDeletionJobs.SingleAsync()).Status);
        }
        await using (var db = factory.CreateDbContext())
        {
            var cleanup = Cleanup(db, factory, directory.Path, new DenyWrite());
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                cleanup.BeginAsync(job.RecordId, Admin()));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                cleanup.ProcessAsync(job.RecordId, Admin()));
            Assert.Equal("LocalReady",
                (await db.VerificationCaseDeletionJobs.SingleAsync()).Status);
        }
    }

    [Fact]
    public async Task SharedPhysicalFileBlocksCleanupBeforeAnyDatabaseRemoval()
    {
        using var directory = new TestDirectory();
        var path = System.IO.Path.Combine(directory.Path, "shared.pdf");
        await File.WriteAllTextAsync(path, "shared");
        var factory = new Factory();
        var (application, record) = Accepted("VA-SHARED", "Model A");
        var (otherApplication, otherRecord) = Accepted("VA-OTHER-SHARED", "Model A");
        var job = VerificationCaseDeletionJob.Prepare(application.Id, record.Id,
            "leader", "Delete this accepted case", DateTime.UtcNow);
        job.MarkLocalReady(DateTime.UtcNow);
        await using (var seed = factory.CreateDbContext())
        {
            seed.VerificationApplications.AddRange(application, otherApplication);
            seed.ModuleRecords.AddRange(record, otherRecord);
            seed.Modules.AddRange(
                new ModuleEntity { Id = record.ModuleId, Code = "verification", Name = "Verification" },
                new ModuleEntity { Id = otherRecord.ModuleId, Code = "other", Name = "Other" });
            seed.SystemOptions.Add(new SystemOption { Id = Guid.NewGuid(),
                Category = "ThreeLevelRootPath", Name = "verification", Value = directory.Path,
                IsEnabled = true });
            seed.ModuleCaseFiles.AddRange(
                new ModuleCaseFile { Id = Guid.NewGuid(), RecordId = record.Id,
                    FilePath = directory.Path, FileName = "shared.pdf", DocumentId = Guid.NewGuid(),
                    VersionNo = 1, FileKind = ModuleCaseFileKinds.Attachment },
                new ModuleCaseFile { Id = Guid.NewGuid(), RecordId = otherRecord.Id,
                    FilePath = directory.Path, FileName = "shared.pdf", DocumentId = Guid.NewGuid(),
                    VersionNo = 1, FileKind = ModuleCaseFileKinds.Attachment });
            seed.VerificationCaseDeletionJobs.Add(job);
            await seed.SaveChangesAsync();
        }
        await using var db = factory.CreateDbContext();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Cleanup(db, factory, directory.Path).BeginAsync(record.Id, Admin()));
        Assert.Contains("shared", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("LocalReady", (await db.VerificationCaseDeletionJobs.SingleAsync()).Status);
        Assert.True(await db.VerificationApplications.AnyAsync(x => x.Id == application.Id));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task RawDataAndKmManifestAreRemovedOnlyForTheirExactRecord()
    {
        using var directory = new TestDirectory();
        var factory = new Factory();
        var (application, record) = Accepted("VA-RAW", "Unique Project");
        var job = VerificationCaseDeletionJob.Prepare(application.Id, record.Id,
            "leader", "Delete this accepted case", DateTime.UtcNow);
        job.MarkLocalReady(DateTime.UtcNow);
        var localProject = System.IO.Path.Combine(directory.Path, "Unique Project");
        var drive = System.IO.Path.GetPathRoot(localProject)!;
        var nasProject = System.IO.Path.Combine(directory.Path,
            localProject[drive.Length..].TrimStart(System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(nasProject);
        var rawPath = System.IO.Path.Combine(nasProject, "rawdata.json");
        var kmPath = System.IO.Path.Combine(nasProject, "km-manifest.json");
        var content = JsonSerializer.Serialize(new { source = new { entityId = record.Id.ToString() },
            project = new { id = record.Id } });
        await File.WriteAllTextAsync(rawPath, content);
        await File.WriteAllTextAsync(kmPath, content);
        await using (var seed = factory.CreateDbContext())
        {
            seed.Modules.Add(new ModuleEntity { Id = record.ModuleId, Code = "verification",
                Name = "Verification" });
            seed.SystemOptions.Add(new SystemOption { Id = Guid.NewGuid(),
                Category = "ThreeLevelRootPath", Name = "verification",
                Value = directory.Path, IsEnabled = true });
            seed.ModuleRecords.Add(record);
            seed.VerificationApplications.Add(application);
            seed.VerificationCaseDeletionJobs.Add(job);
            await seed.SaveChangesAsync();
        }
        await using (var db = factory.CreateDbContext())
        {
            var cleanup = Cleanup(db, factory, directory.Path);
            var prepared = await cleanup.BeginAsync(record.Id, Admin());
            Assert.Contains("rawdata.json", prepared.CleanupManifestJson);
            Assert.Contains("km-manifest.json", prepared.CleanupManifestJson);
            Assert.Equal("Completed", (await cleanup.ProcessAsync(record.Id, Admin())).Status);
        }
        Assert.False(File.Exists(rawPath));
        Assert.False(File.Exists(kmPath));
    }

    [Fact]
    public async Task PostgreSqlCleanupHonorsForeignKeysAndKeepsSameNameOtherApplication()
    {
        var connection = Environment.GetEnvironmentVariable("CSIT_CASE_DELETE_TEST_DB");
        if (connection is null) return;
        if (!connection.Contains("Host=127.0.0.1;Port=55432;Database=csit_deletion_migration_test;",
                StringComparison.Ordinal))
            throw new InvalidOperationException("Only the isolated case deletion test database is allowed.");
        using var directory = new TestDirectory();
        var factory = new Factory(connection);
        var (first, record) = Accepted("VA-PG-" + Guid.NewGuid().ToString("N")[..12], "Same Model");
        var (second, other) = Accepted("VA-PG-" + Guid.NewGuid().ToString("N")[..12], "Same Model");
        var now = DateTime.UtcNow;
        var firstCaseNo = "C-" + record.Id.ToString("N")[..10];
        var firstTaskNo = "T-" + record.Id.ToString("N")[..10];
        var otherCaseNo = "C-" + other.Id.ToString("N")[..10];
        var otherTaskNo = "T-" + other.Id.ToString("N")[..10];
        var firstCase = new ModuleRecordCase { Id = Guid.NewGuid(), RecordId = record.Id,
            CaseNo = firstCaseNo, Name = "Radio", Status = "Open", CreatedAt = now, UpdatedAt = now };
        var firstTask = new ModuleRecordTask { Id = Guid.NewGuid(), CaseId = firstCase.Id,
            TaskNo = firstTaskNo, Name = "Wi-Fi", Status = "Open", CreatedAt = now, UpdatedAt = now };
        var otherCase = new ModuleRecordCase { Id = Guid.NewGuid(), RecordId = other.Id,
            CaseNo = otherCaseNo, Name = "Radio", Status = "Open", CreatedAt = now, UpdatedAt = now };
        var otherTask = new ModuleRecordTask { Id = Guid.NewGuid(), CaseId = otherCase.Id,
            TaskNo = otherTaskNo, Name = "Wi-Fi", Status = "Open", CreatedAt = now, UpdatedAt = now };

        var firstFolder = new DocumentFolder { Id = Guid.NewGuid(), Level = 1,
            Name = "first-" + first.Id.ToString("N"), CreatedAt = now, UpdatedAt = now };
        var otherFolder = new DocumentFolder { Id = Guid.NewGuid(), Level = 1,
            Name = "other-" + second.Id.ToString("N"), CreatedAt = now, UpdatedAt = now };
        var firstLibraryPath = Path.Combine(directory.Path, "library", first.Id.ToString("N"), "application.pdf");
        var otherLibraryPath = Path.Combine(directory.Path, "library", second.Id.ToString("N"), "application.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(firstLibraryPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(otherLibraryPath)!);
        await File.WriteAllTextAsync(firstLibraryPath, "first archived application");
        await File.WriteAllTextAsync(otherLibraryPath, "other archived application");
        var firstDocument = Document(firstFolder.Id, firstLibraryPath, directory.Path, now);
        var otherDocument = Document(otherFolder.Id, otherLibraryPath, directory.Path, now);
        var firstAttachment = Attachment(first.Id, firstDocument, now);
        var otherAttachment = Attachment(second.Id, otherDocument, now);

        var reportDocumentId = Guid.NewGuid();
        var firstReportV1Path = Path.Combine(directory.Path, "cases", record.Id.ToString("N"), "report-v1.pdf");
        var firstReportV2Path = Path.Combine(directory.Path, "cases", record.Id.ToString("N"), "report-v2.pdf");
        var firstNasPath = Path.Combine(directory.Path, "nas", record.Id.ToString("N"), "report-v1.pdf");
        var otherReportPath = Path.Combine(directory.Path, "cases", other.Id.ToString("N"), "report.pdf");
        foreach (var path in new[] { firstReportV1Path, firstReportV2Path, firstNasPath, otherReportPath })
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(firstReportV1Path, "first report version one");
        await File.WriteAllTextAsync(firstReportV2Path, "first report version two");
        await File.WriteAllTextAsync(firstNasPath, "first report version one");
        await File.WriteAllTextAsync(otherReportPath, "other report");
        var firstReportV1 = Report(record.Id, firstCase.Id, firstTask.Id, firstCaseNo,
            firstTaskNo, reportDocumentId, 1, firstReportV1Path, now, isFinal: false, firstNasPath);
        var firstReportV2 = Report(record.Id, firstCase.Id, firstTask.Id, firstCaseNo,
            firstTaskNo, reportDocumentId, 2, firstReportV2Path, now, isFinal: true);
        var otherReport = Report(other.Id, otherCase.Id, otherTask.Id, otherCaseNo,
            otherTaskNo, Guid.NewGuid(), 1, otherReportPath, now, isFinal: true);
        var firstReportOutbox = KmIntegrationOutbox.ForCommittedTestReport(firstReportV1, now);
        var sentLease = Guid.NewGuid();
        firstReportOutbox.Claim(sentLease, now, TimeSpan.FromMinutes(2));
        firstReportOutbox.Sent(sentLease, "KM-INGEST-" + Guid.NewGuid().ToString("N"), now);
        var firstV2Outbox = KmIntegrationOutbox.ForCommittedTestReport(firstReportV2, now);
        var firstAttachmentOutbox = KmIntegrationOutbox.ForArchivedVerificationAttachment(
            first, firstAttachment, now);
        foreach (var delivered in new[] { firstV2Outbox, firstAttachmentOutbox })
        {
            var lease = Guid.NewGuid();
            delivered.Claim(lease, now, TimeSpan.FromMinutes(2));
            delivered.Sent(lease, "KM-INGEST-" + Guid.NewGuid().ToString("N"), now);
        }
        var otherOutbox = KmIntegrationOutbox.ForCommittedTestReport(otherReport, now);
        var retainedAudit = ApprovalAuditEvent.Create(first.Id, "verification", "verification",
            ApprovalAuditActions.Approved, "leader", now, null, null,
            details: "Synthetic accepted application audit.");
        await using (var seed = factory.CreateDbContext())
        {
            seed.Modules.AddRange(new ModuleEntity { Id = record.ModuleId, Code = "test-" + record.Id.ToString("N"),
                Name = "Test", DisplayName = "Test", RoutePrefix = "test" },
                new ModuleEntity { Id = other.ModuleId, Code = "test-" + other.Id.ToString("N"),
                    Name = "Test", DisplayName = "Test", RoutePrefix = "test" });
            foreach (var app in new[] { first, second })
            {
                seed.SystemOptions.Add(new SystemOption { Id = app.RoutingDepartmentOptionId!.Value,
                    Category = "Department", Name = "test-" + Guid.NewGuid().ToString("N"),
                    Value = "RD", IsEnabled = true });
                seed.SystemOptions.Add(new SystemOption { Id = app.TeamOptionId!.Value,
                    Category = "Team", Name = "test-" + Guid.NewGuid().ToString("N"),
                    Value = "TEAM-A", IsEnabled = true });
            }
            seed.ModuleRecords.AddRange(record, other);
            seed.VerificationApplications.AddRange(first, second);
            seed.ModuleRecordCases.AddRange(firstCase, otherCase);
            seed.ModuleRecordTasks.AddRange(firstTask, otherTask);
            seed.DocumentFolders.AddRange(firstFolder, otherFolder);
            seed.DocumentLibraryFiles.AddRange(firstDocument, otherDocument);
            seed.DocumentProcessingJobs.AddRange(
                new DocumentProcessingJob { Id = Guid.NewGuid(), DocumentFileId = firstDocument.Id,
                    Status = DocumentProcessingStatuses.Completed, CreatedAt = now, UpdatedAt = now },
                new DocumentProcessingJob { Id = Guid.NewGuid(), DocumentFileId = otherDocument.Id,
                    Status = DocumentProcessingStatuses.Completed, CreatedAt = now, UpdatedAt = now });
            seed.VerificationApplicationFiles.AddRange(firstAttachment, otherAttachment);
            seed.ModuleCaseFiles.AddRange(firstReportV1, firstReportV2, otherReport);
            seed.KmIntegrationOutbox.AddRange(firstReportOutbox, firstV2Outbox,
                firstAttachmentOutbox, otherOutbox);
            seed.ApprovalAuditEvents.Add(retainedAudit);
            seed.SystemOptions.Add(new SystemOption { Id = Guid.NewGuid(),
                Category = "ThreeLevelRootPath", Name = "test-" + record.Id.ToString("N"),
                Value = directory.Path, IsEnabled = true });
            await seed.SaveChangesAsync();
        }
        Guid deletionJobId;
        await using (var db = factory.CreateDbContext())
        {
            var archive = Archive(factory, directory.Path);
            var confirmation = new VerificationCaseDeletionConfirmationService(db,
                new VerificationCaseDeletionPreparationService(db, new AllowWrite()), archive,
                Options.Create(new KmDeletionOptions { Enabled = true }));
            var confirmed = await confirmation.ConfirmAsync(record.Id,
                first.ApplicationNo, "Delete exact test case", Manager());
            Assert.Equal("WaitingForKm", confirmed.Status);
            deletionJobId = confirmed.Id;
        }
        await using (var db = factory.CreateDbContext())
        {
            var cleanup = Cleanup(db, factory, directory.Path);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                cleanup.BeginAsync(record.Id, Admin()));
            Assert.Equal("WaitingForKm",
                (await db.VerificationCaseDeletionJobs.SingleAsync(x => x.RecordId == record.Id)).Status);
        }
        await using var mockKm = await MockKmDeletionServer.StartAsync();
        await using var callbackHost = await CsitDeletionCallbackTestServer.StartAsync(factory);
        var dispatchOptions = Options.Create(new KmIntegrationOptions
        {
            BaseUrl = "https://km.mock.test/", TimeoutSeconds = 2, LeaseSeconds = 30,
            MaxRetryCount = 3
        });
        var deletionOptions = Options.Create(new KmDeletionOptions
            { Enabled = true, Scope = "csit.document.delete" });
        var dispatcher = new KmDeletionDispatchService(factory,
            new KmDeletionClient(mockKm.Client, dispatchOptions, deletionOptions,
                new MockDeletionToken()), deletionOptions, dispatchOptions);
        Assert.Equal(3, await dispatcher.ProcessJobAsync(deletionJobId));

        Guid deletionTargetId;
        KmDeletionCallback completedCallback = null!;
        List<Guid> deletionTargetIds;
        await using (var verifyAccepted = factory.CreateDbContext())
        {
            var acceptedJob = await verifyAccepted.VerificationCaseDeletionJobs.Include(x => x.Targets)
                .SingleAsync(x => x.RecordId == record.Id);
            Assert.Equal("WaitingForKm", acceptedJob.Status);
            Assert.All(acceptedJob.Targets, target => Assert.Equal("Accepted", target.Status));
            Assert.All(acceptedJob.Targets, target => Assert.NotNull(target.KmDeletionJobId));
            deletionTargetIds = acceptedJob.Targets.Select(x => x.Id).ToList();
            deletionTargetId = acceptedJob.Targets
                .Single(x => x.SourceEntityId == firstReportV1.Id).Id;
        }
        Assert.Equal(3, mockKm.DeletionRequestIds.Count);

        foreach (var targetId in deletionTargetIds)
        {
            var processing = mockKm.CreateCallback(targetId, "Processing");
            using var processingResponse = await MockKmDeletionServer.SendCallbackAsync(
                callbackHost.Client, processing);
            Assert.Equal(System.Net.HttpStatusCode.NoContent, processingResponse.StatusCode);
            var completed = mockKm.CreateCallback(targetId, "Completed");
            using var completedResponse = await MockKmDeletionServer.SendCallbackAsync(
                callbackHost.Client, completed);
            Assert.Equal(System.Net.HttpStatusCode.NoContent, completedResponse.StatusCode);
            if (targetId == deletionTargetId) completedCallback = completed;
        }
        using (var duplicate = await MockKmDeletionServer.SendCallbackAsync(
            callbackHost.Client, completedCallback))
            Assert.Equal(System.Net.HttpStatusCode.NoContent, duplicate.StatusCode);
        await using (var verified = factory.CreateDbContext())
        {
            var verifiedJob = await verified.VerificationCaseDeletionJobs.Include(x => x.Targets)
                .SingleAsync(x => x.RecordId == record.Id);
            Assert.Equal("KmVerified", verifiedJob.Status);
            Assert.All(verifiedJob.Targets, target =>
            {
                Assert.Equal("Completed", target.Status);
                Assert.NotNull(target.KmVerifiedAt);
            });
        }
        await using (var db = factory.CreateDbContext())
        {
            var cleanup = Cleanup(db, factory, directory.Path);
            await cleanup.BeginAsync(record.Id, Admin());
            await using (var locker = factory.CreateDbContext())
            {
                await locker.Database.OpenConnectionAsync();
                await using var command = locker.Database.GetDbConnection().CreateCommand();
                command.CommandText = "SELECT pg_advisory_lock(@key)";
                var parameter = command.CreateParameter();
                parameter.ParameterName = "key";
                parameter.Value = BitConverter.ToInt64(record.Id.ToByteArray(), 0) ^ 0x4353495444454C45L;
                command.Parameters.Add(parameter);
                await command.ExecuteScalarAsync();
                await Assert.ThrowsAsync<InvalidOperationException>(() => cleanup.ProcessAsync(record.Id, Admin()));
                command.CommandText = "SELECT pg_advisory_unlock(@key)";
                await command.ExecuteScalarAsync();
            }
            File.SetAttributes(firstNasPath, FileAttributes.ReadOnly);
            try
            {
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                    cleanup.ProcessAsync(record.Id, Admin()));
            }
            finally
            {
                if (File.Exists(firstNasPath)) File.SetAttributes(firstNasPath, FileAttributes.Normal);
            }
            var partial = await db.VerificationCaseDeletionJobs.SingleAsync(x => x.RecordId == record.Id);
            Assert.NotNull(partial.DbCleanedAt);
            Assert.Equal("LocalCleanup", partial.Status);
            Assert.True(File.Exists(firstNasPath));
        }
        await using (var retry = factory.CreateDbContext())
        {
            Assert.Equal("Completed", (await Cleanup(retry, factory, directory.Path)
                .ProcessAsync(record.Id, Admin())).Status);
        }
        await using (var verify = factory.CreateDbContext())
        {
            Assert.False(await verify.VerificationApplications.AnyAsync(x => x.Id == first.Id));
            Assert.False(await verify.ModuleRecords.AnyAsync(x => x.Id == record.Id));
            Assert.True(await verify.VerificationApplications.AnyAsync(x => x.Id == second.Id));
            Assert.True(await verify.ModuleRecords.AnyAsync(x => x.Id == other.Id));
            Assert.False(await verify.ModuleRecordCases.AnyAsync(x => x.Id == firstCase.Id));
            Assert.False(await verify.ModuleRecordTasks.AnyAsync(x => x.Id == firstTask.Id));
            Assert.False(await verify.ModuleCaseFiles.AnyAsync(x => x.RecordId == record.Id));
            Assert.False(await verify.VerificationApplicationFiles.AnyAsync(x =>
                x.VerificationApplicationId == first.Id));
            Assert.False(await verify.DocumentLibraryFiles.AnyAsync(x => x.Id == firstDocument.Id));
            Assert.False(await verify.DocumentProcessingJobs.AnyAsync(x =>
                x.DocumentFileId == firstDocument.Id));
            Assert.False(await verify.KmIntegrationOutbox.AnyAsync(x =>
                x.SourceEntityId == firstReportV1.Id || x.SourceEntityId == firstReportV2.Id ||
                x.SourceEntityId == firstAttachment.Id));
            Assert.True(await verify.ApprovalAuditEvents.AnyAsync(x => x.Id == retainedAudit.Id));
            Assert.True(await verify.VerificationCaseDeletionJobs.AnyAsync(x =>
                x.RecordId == record.Id && x.Status == "Completed"));
            Assert.True(await verify.VerificationCaseDeletionTargets.AnyAsync(x => x.Id == deletionTargetId));
            Assert.True(await verify.VerificationCaseDeletionCallbacks.AnyAsync(x =>
                x.CallbackId == completedCallback.CallbackId));
            Assert.True(await verify.ModuleRecordCases.AnyAsync(x => x.Id == otherCase.Id));
            Assert.True(await verify.ModuleRecordTasks.AnyAsync(x => x.Id == otherTask.Id));
            Assert.True(await verify.ModuleCaseFiles.AnyAsync(x => x.Id == otherReport.Id));
            Assert.True(await verify.DocumentLibraryFiles.AnyAsync(x => x.Id == otherDocument.Id));
            Assert.True(await verify.KmIntegrationOutbox.AnyAsync(x => x.Id == otherOutbox.Id));
        }
        foreach (var deleted in new[] { firstLibraryPath, firstReportV1Path,
            firstReportV2Path, firstNasPath }) Assert.False(File.Exists(deleted));
        Assert.True(File.Exists(otherLibraryPath));
        Assert.True(File.Exists(otherReportPath));
    }

    private static DocumentLibraryFile Document(Guid folderId, string path, string root, DateTime now)
    {
        var bytes = File.ReadAllBytes(path);
        return new DocumentLibraryFile { Id = Guid.NewGuid(), FolderId = folderId,
            OriginalFileName = Path.GetFileName(path),
            StandardFileName = Guid.NewGuid().ToString("N") + ".pdf",
            ContentType = "application/pdf", UploadedBy = "leader", UploadedAt = now,
            FileSize = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            RelativePath = Path.GetRelativePath(root, path), StorageArea = DocumentStorageAreas.Library };
    }

    private static VerificationApplicationFile Attachment(Guid applicationId,
        DocumentLibraryFile document, DateTime now) => new()
    {
        Id = Guid.NewGuid(), VerificationApplicationId = applicationId,
        FileName = document.OriginalFileName,
        FilePath = Path.Combine("verification-applications", applicationId.ToString("N"),
            document.OriginalFileName),
        ContentType = document.ContentType, FileSize = document.FileSize, Sha256 = document.Sha256,
        UploadedBy = "applicant", CreatedAt = now, DocumentLibraryFileId = document.Id,
        DocumentLibraryFile = document
    };

    private static ModuleCaseFile Report(Guid recordId, Guid caseId, Guid taskId,
        string caseNo, string taskNo, Guid documentId, int version, string path, DateTime now, bool isFinal,
        string? nasPath = null) => new()
    {
        Id = Guid.NewGuid(), RecordId = recordId, CaseId = caseId, CaseNo = caseNo,
        TaskId = taskId, TaskNo = taskNo, FileName = Path.GetFileName(path),
        FilePath = Path.GetDirectoryName(path)!, ContentType = "application/pdf",
        FileSize = new FileInfo(path).Length, UploadEmp = "engineer", CreatedAt = now,
        DocumentId = documentId, VersionNo = version, FileKind = ModuleCaseFileKinds.TestReport,
        ReviewStatus = TestReportReviewStatuses.Approved, ReviewedAt = now, ReviewedBy = "leader",
        IsFinal = isFinal, FinalizedAt = isFinal ? now : null, FinalizedBy = isFinal ? "leader" : null,
        UpdatedAt = now, IsRawDataExported = true, RawDataExportedAt = now,
        NasFilePath = nasPath
    };

    private static VerificationCaseLocalCleanupService Cleanup(AppDbContext db, Factory factory,
        string root, IBusinessWriteAuthorizationGuard? businessWrite = null,
        bool deletionEnabled = true) =>
        new(db, businessWrite ?? new AllowWrite(), new AdminAuthorization(),
        Options.Create(new KmDeletionOptions { Enabled = deletionEnabled }),
        Options.Create(new DocumentStorageOptions { RootPath = root }),
        Options.Create(new DocumentStagingOptions { RootPath = root }),
        Options.Create(new RawDataSettings { NasRootPath = root }), Archive(factory, root));

    private static VerificationAttachmentArchiveProcessor Archive(Factory factory, string root) =>
        new(factory, Options.Create(new DocumentStorageOptions { RootPath = root }),
            Options.Create(new DocumentStagingOptions { RootPath = root }),
            new VerificationAttachmentArchiveJournal(
                Options.Create(new DocumentStagingOptions { RootPath = root })),
            NullLogger<VerificationAttachmentArchiveProcessor>.Instance);

    private static (VerificationApplication, ModuleRecord) Accepted(string no, string name)
    {
        var record = new ModuleRecord { Id = Guid.NewGuid(), ModuleId = Guid.NewGuid(),
            RecordNo = no, Name = name, Status = "Open" };
        var department = Guid.NewGuid(); var team = Guid.NewGuid(); var now = DateTime.UtcNow;
        var app = VerificationApplication.CreateDraft(Guid.NewGuid(), no, department, team,
            "applicant", "Applicant", "applicant@example.test", "RD", null,
            new VerificationApplicationContent { ProjectName = name, ProductModel = name, Customer = "Customer",
                ValidationRequirement = "Validation", RequestedFinishDate = new DateOnly(2026, 11, 1) }, now);
        app.Submit(new VerificationApplicationRouting(department, "RD", "RD", team,
            "TEAM-A", "Team A", "verification", "leader", "Leader"), now);
        app.Accept(record.Id, new DateOnly(2026, 11, 2), "leader", now);
        return (app, record);
    }

    private static ClaimsPrincipal Manager(string account = "leader") => new(new ClaimsIdentity([
        new Claim("account", account),
        new Claim(SystemAuthorization.AccessScopeClaim, SystemAuthorization.AccessScopes.CsitStaff)
    ], "test"));
    private static ClaimsPrincipal Admin() => new(new ClaimsIdentity([
        new Claim("account", "admin"), new Claim(ClaimTypes.Role, "Admin"),
        new Claim(SystemAuthorization.AccessScopeClaim, SystemAuthorization.AccessScopes.CsitStaff)
    ], "test"));

    private sealed class AllowWrite : IBusinessWriteAuthorizationGuard
    {
        public Task DemandAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class DenyWrite : IBusinessWriteAuthorizationGuard
    {
        public Task DemandAsync(CancellationToken cancellationToken = default) =>
            throw new UnauthorizedAccessException("BusinessWrite denied.");
    }

    private sealed class MockDeletionToken : IKmDeletionAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) =>
            Task.FromResult("mock-km-submit");
    }

    private sealed class AdminAuthorization : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource,
            IEnumerable<IAuthorizationRequirement> requirements) => Task.FromResult(
                user.IsInRole("Admin") ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource,
            string policyName) => Task.FromResult(user.IsInRole("Admin")
                ? AuthorizationResult.Success() : AuthorizationResult.Failed());
    }

    private sealed class Factory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> options;
        public Factory(string? connection = null)
        {
            var builder = new DbContextOptionsBuilder<AppDbContext>();
            if (connection is null) builder.UseInMemoryDatabase(Guid.NewGuid().ToString("N"));
            else builder.UseNpgsql(connection);
            options = builder.Options;
        }
        public AppDbContext CreateDbContext() => new(options);
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "csit-case-delete-test-" + Guid.NewGuid().ToString("N"));
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            var full = System.IO.Path.GetFullPath(Path);
            var temp = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
            if (!full.StartsWith(temp + "csit-case-delete-test-", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Test cleanup escaped its temporary root.");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }
}
