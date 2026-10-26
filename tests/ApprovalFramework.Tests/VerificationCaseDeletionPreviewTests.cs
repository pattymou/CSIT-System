using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Config;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Approval;

namespace ApprovalFramework.Tests;

public sealed class VerificationCaseDeletionPreviewTests
{
    [Fact]
    public async Task PreviewInventoriesExactApplicationFilesVersionsKmAndPendingUploadsWithoutMutation()
    {
        var localFile = Path.GetTempFileName();
        try
        {
            await using var db = NewDb();
            var (first, record, task, team) = Accepted("VA-FIRST", "SAME-MODEL");
            var (second, otherRecord, _, _) = Accepted("VA-SECOND", "SAME-MODEL");
            var documentFile = new DocumentLibraryFile
            {
                Id = Guid.NewGuid(), FolderId = Guid.NewGuid(), OriginalFileName = "request.pdf",
                StandardFileName = Path.GetFileName(localFile), RelativePath = Path.GetFileName(localFile),
                StorageArea = DocumentStorageAreas.Library, FileSize = 1, Sha256 = "hash"
            };
            var attachment = new VerificationApplicationFile
            {
                Id = Guid.NewGuid(), VerificationApplicationId = first.Id, FileName = "request.pdf",
                FilePath = "verification-applications/original.pdf", FileSize = 1, UploadedBy = "applicant",
                DocumentLibraryFileId = documentFile.Id, DocumentLibraryFile = documentFile
            };
            var reportDocumentId = Guid.NewGuid();
            var versions = Enumerable.Range(1, 3).Select(version => new ModuleCaseFile
            {
                Id = Guid.NewGuid(), RecordId = record.Id, CaseId = task.CaseId, TaskId = task.Id,
                CaseNo = "RADIO", TaskNo = task.TaskNo, FileName = Path.GetFileName(localFile),
                FilePath = Path.GetDirectoryName(localFile)!, FileSize = 1,
                DocumentId = reportDocumentId, VersionNo = version,
                FileKind = ModuleCaseFileKinds.TestReport, ReviewStatus = TestReportReviewStatuses.Approved,
                IsRawDataExported = true, IsFinal = version == 3
            }).ToArray();
            var otherFile = new ModuleCaseFile
            {
                Id = Guid.NewGuid(), RecordId = otherRecord.Id, FileName = "other.pdf",
                FilePath = Path.GetDirectoryName(localFile)!, DocumentId = Guid.NewGuid(),
                VersionNo = 1, FileKind = ModuleCaseFileKinds.Attachment
            };
            var kmPending = KmIntegrationOutbox.ForCommittedTestReport(versions[0], DateTime.UtcNow);
            var kmDispatching = KmIntegrationOutbox.ForCommittedTestReport(versions[1], DateTime.UtcNow);
            kmDispatching.Claim(Guid.NewGuid(), DateTime.UtcNow, TimeSpan.FromMinutes(2));
            var kmCompleted = KmIntegrationOutbox.ForCommittedTestReport(versions[2], DateTime.UtcNow);
            var lease = Guid.NewGuid();
            kmCompleted.Claim(lease, DateTime.UtcNow, TimeSpan.FromMinutes(2));
            kmCompleted.Sent(lease, "job-123", DateTime.UtcNow);
            kmCompleted.CallbackCompleted(DateTime.UtcNow);
            var archivedAttachmentOutbox = KmIntegrationOutbox.ForArchivedVerificationAttachment(
                first, attachment, DateTime.UtcNow);
            var pending = PendingSubmission.Create(Guid.NewGuid(), "submission-key", "verification", "verification",
                "applicant", "RD", team, "TEAM-A", nameof(ThreeLevelUploadKind.TestReportVersion),
                new ThreeLevelUploadTarget(ThreeLevelUploadKind.TestReportVersion, versions[0].Id).ContextId,
                "report", null, DateTime.UtcNow);
            db.AddRange(first, record, second, otherRecord, attachment, otherFile, pending,
                kmPending, kmDispatching, kmCompleted, archivedAttachmentOutbox);
            db.ModuleCaseFiles.AddRange(versions);
            db.DocumentProcessingJobs.Add(new DocumentProcessingJob
            {
                Id = Guid.NewGuid(), DocumentFileId = documentFile.Id,
                Status = DocumentProcessingStatuses.Completed
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var preview = await Service(db, Path.GetDirectoryName(localFile)!).GetAsync(record.Id, Manager());

            Assert.Equal(first.Id, preview.ApplicationId);
            Assert.Equal(record.Id, preview.RecordId);
            Assert.False(preview.CanExecute);
            Assert.Equal([1, 2, 3], preview.Items.Where(x => x.Kind == "TestReport")
                .Select(x => x.Version!.Value).OrderBy(x => x).ToArray());
            Assert.Contains(preview.Items, x => x.Kind == "VerificationApplicationAttachment" && x.Id == attachment.Id);
            Assert.Contains(preview.Items, x => x.Kind == "DocumentLibraryFile" && x.Id == documentFile.Id);
            Assert.Contains(preview.Items, x => x.Kind == "PhysicalDocument" && x.PhysicalExists == true);
            Assert.Contains(preview.Items, x => x.Kind == "PhysicalCaseFile" && x.PhysicalExists == true);
            Assert.Contains(preview.Items, x => x.Kind == "PendingSubmission" && x.Id == pending.Id);
            Assert.Equal(["Completed", "Dispatching", "Pending", "Pending"], preview.Items.Where(x => x.Kind == "KmOutbox")
                .Select(x => x.Status!).OrderBy(x => x).ToArray());
            Assert.Contains(preview.Items, x => x.Kind == "KmOutbox" && x.ParentId == attachment.Id &&
                x.DocumentId == documentFile.Id);
            Assert.Contains(preview.BlockingReasons, x => x.Contains("TestReport"));
            Assert.Contains(preview.BlockingReasons, x => x.Contains("KM"));
            Assert.DoesNotContain(preview.Items, x => x.Id == second.Id || x.Id == otherRecord.Id || x.Id == otherFile.Id);
            Assert.Equal(2, await db.VerificationApplications.CountAsync());
            Assert.Equal(4, await db.ModuleCaseFiles.CountAsync());
        }
        finally { File.Delete(localFile); }
    }

    [Fact]
    public async Task PreviewRequiresAssignedManagerAndBusinessWrite()
    {
        await using var db = NewDb();
        var (application, record, _, _) = Accepted("VA-ACCESS", "MODEL-A");
        db.AddRange(application, record);
        await db.SaveChangesAsync();
        var preview = Service(db, Path.GetTempPath());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => preview.GetAsync(record.Id, Manager("staff")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => preview.GetAsync(record.Id, Manager("leader", scope: false)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Service(db, Path.GetTempPath(), allowWrite: false).GetAsync(record.Id, Manager()));
    }

    [Fact]
    public async Task PreviewRejectsRelativePathEscapeWithoutReadingOutsideRoot()
    {
        await using var db = NewDb();
        var (application, record, _, _) = Accepted("VA-PATH", "MODEL-A");
        db.AddRange(application, record, new VerificationApplicationFile
        {
            Id = Guid.NewGuid(), VerificationApplicationId = application.Id, FileName = "outside.pdf",
            FilePath = "../outside.pdf", UploadedBy = "applicant"
        });
        await db.SaveChangesAsync();
        var preview = await Service(db, Path.GetTempPath()).GetAsync(record.Id, Manager());
        Assert.Contains(preview.Items, x => x.Kind == "PhysicalStaging" && x.PhysicalExists is null);
        Assert.Contains(preview.BlockingReasons, x => x.Contains("路徑"));
    }

    private static (VerificationApplication, ModuleRecord, ModuleRecordTask, Guid) Accepted(string number, string model)
    {
        var team = Guid.NewGuid();
        var department = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var record = new ModuleRecord
        {
            Id = Guid.NewGuid(), ModuleId = Guid.NewGuid(), RecordNo = $"REC-{number}", Name = model,
            CustomerProductName = model, Status = "Open", CreatedAt = now, UpdatedAt = now
        };
        var recordCase = new ModuleRecordCase
        {
            Id = Guid.NewGuid(), RecordId = record.Id, CaseNo = $"{number}-RADIO", Name = "Radio",
            Status = "Open", SourceTestCategoryCode = "RADIO", CreatedAt = now, UpdatedAt = now
        };
        var task = new ModuleRecordTask
        {
            Id = Guid.NewGuid(), CaseId = recordCase.Id, TaskNo = $"{number}-WIFI", Name = "Wi-Fi",
            Status = "Open", SourceTestItemCode = "WIFI", CreatedAt = now, UpdatedAt = now
        };
        recordCase.Tasks.Add(task);
        record.Cases.Add(recordCase);
        var application = VerificationApplication.CreateDraft(Guid.NewGuid(), number, department, team,
            "applicant", "Applicant", "a@example.test", "RD", null,
            new VerificationApplicationContent
            {
                ProjectName = model, ProductModel = model, Customer = "Customer",
                ValidationRequirement = "Radio validation",
                RequestedFinishDate = new DateOnly(2026, 11, 30)
            }, now);
        application.Submit(new VerificationApplicationRouting(department, "DA40", "DA40", team,
            "TEAM-A", "Team A", "verification", "leader", "Leader"), now);
        application.Accept(record.Id, new DateOnly(2026, 12, 1), "leader", now);
        return (application, record, task, team);
    }

    private static ClaimsPrincipal Manager(string account = "leader", bool scope = true) => new(new ClaimsIdentity(
        scope ? [new Claim("account", account), new Claim(SystemAuthorization.AccessScopeClaim,
            SystemAuthorization.AccessScopes.CsitStaff)] : [new Claim("account", account)], "test"));

    private static IVerificationCaseDeletionPreviewService Service(
        AppDbContext db, string root, bool allowWrite = true) => new VerificationCaseDeletionPreviewService(
        db, allowWrite ? new AllowWrite() : new DenyWrite(),
        Options.Create(new DocumentStorageOptions { RootPath = root }),
        Options.Create(new DocumentStagingOptions { RootPath = root }));

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
}
