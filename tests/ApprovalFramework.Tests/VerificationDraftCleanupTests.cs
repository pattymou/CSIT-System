using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Config;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Implementations;

namespace ApprovalFramework.Tests;

public sealed class VerificationDraftCleanupTests
{
    [Fact]
    public async Task DryRun_UsesExactUtcCutoff_ReportsExclusions_AndDoesNotDelete()
    {
        var now = new DateTime(2026, 10, 24, 8, 0, 0, DateTimeKind.Utc);
        var cutoff = now.AddDays(-15);
        var fixture = Fixture(dryRun: true);
        await using (var db = fixture.Factory.CreateDbContext())
        {
            var eligible = DynamicDraft("ELIGIBLE", cutoff);
            var recent = DynamicDraft("RECENT", cutoff.AddTicks(1));
            var legacy = LegacyDraft("LEGACY", cutoff.AddDays(-1));
            var submitted = DynamicDraft("SUBMITTED", cutoff.AddDays(-1));
            db.Entry(submitted).Property(x => x.Status).CurrentValue = VerificationApplicationStatus.Submitted;
            db.AddRange(eligible, recent, legacy, submitted);

            var protectedPath = $"verification-applications/{eligible.Id:N}/protected.txt";
            var deletablePath = $"verification-applications/{eligible.Id:N}/deletable.txt";
            var folder = Folder();
            var document = Document(folder.Id, protectedPath);
            db.AddRange(folder, document,
                Attachment(eligible.Id, protectedPath, document.Id),
                Attachment(eligible.Id, deletablePath));
            await db.SaveChangesAsync();
        }

        var report = await fixture.Worker.RunAsync(now);

        Assert.Equal(cutoff, report.CutoffUtc);
        Assert.Equal(1, report.EligibleDrafts);
        Assert.Equal(2, report.EligibleAttachments);
        Assert.Equal(1, report.DeletableFiles);
        Assert.Equal(1, report.ProtectedFiles);
        Assert.Equal(1, report.ExcludedRecentDrafts);
        Assert.Equal(1, report.ExcludedLegacyDrafts);
        Assert.Equal(1, report.ExcludedNonDraftApplications);
        await using var verification = fixture.Factory.CreateDbContext();
        Assert.Equal(4, await verification.VerificationApplications.CountAsync());
    }

    [Fact]
    public async Task ActualCleanup_LeavesDurableRetryAfterFileFailure_ThenRecoversIdempotently()
    {
        var now = new DateTime(2026, 10, 24, 8, 0, 0, DateTimeKind.Utc);
        var fileSystem = new RecordingFileSystem { FailuresRemaining = 1 };
        var fixture = Fixture(dryRun: false, deletionConfirmed: true, fileSystem);
        Guid applicationId;
        await using (var db = fixture.Factory.CreateDbContext())
        {
            var application = DynamicDraft("RETRY", now.AddDays(-16));
            applicationId = application.Id;
            db.Add(application);
            db.Add(Attachment(application.Id, $"verification-applications/{application.Id:N}/retry.txt"));
            await db.SaveChangesAsync();
        }

        var first = await fixture.Worker.RunAsync(now);
        await using (var db = fixture.Factory.CreateDbContext())
            Assert.False(await db.VerificationApplications.AnyAsync(x => x.Id == applicationId));
        Assert.Equal(1, first.PendingRetries);
        Assert.Single(await fixture.Journal.ReadAsync(CancellationToken.None));

        var second = await fixture.Worker.RunAsync(now.AddDays(1));
        Assert.Equal(0, second.PendingRetries);
        Assert.Empty(await fixture.Journal.ReadAsync(CancellationToken.None));
        Assert.Equal(2, fileSystem.Attempts);

        await fixture.Worker.RunAsync(now.AddDays(2));
        Assert.Equal(2, fileSystem.Attempts);
    }

    [Fact]
    public async Task ActualCleanup_DeletesDatabaseRowAndExclusivePhysicalFile()
    {
        var now = new DateTime(2026, 10, 24, 8, 0, 0, DateTimeKind.Utc);
        var fixture = Fixture(dryRun: false, deletionConfirmed: true, usePhysicalFileSystem: true);
        var application = DynamicDraft("DELETE", now.AddDays(-16));
        var relativePath = $"verification-applications/{application.Id:N}/delete.txt";
        var absolutePath = Path.Combine(fixture.StagingRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        await File.WriteAllTextAsync(absolutePath, "isolated test file");
        await using (var db = fixture.Factory.CreateDbContext())
        {
            db.Add(application);
            db.Add(Attachment(application.Id, relativePath));
            await db.SaveChangesAsync();
        }

        await fixture.Worker.RunAsync(now);

        await using var verification = fixture.Factory.CreateDbContext();
        Assert.False(await verification.VerificationApplications.AnyAsync(x => x.Id == application.Id));
        Assert.False(File.Exists(absolutePath));
        Assert.Empty(await fixture.Journal.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ActualCleanup_RequiresSeparateDeletionConfirmation()
    {
        var now = new DateTime(2026, 10, 24, 8, 0, 0, DateTimeKind.Utc);
        var fixture = Fixture(dryRun: false, deletionConfirmed: false);
        var application = DynamicDraft("NOT-CONFIRMED", now.AddDays(-16));
        await using (var db = fixture.Factory.CreateDbContext())
        {
            db.Add(application);
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Worker.RunAsync(now));

        await using var verification = fixture.Factory.CreateDbContext();
        Assert.True(await verification.VerificationApplications.AnyAsync(x => x.Id == application.Id));
    }

    [Fact]
    public async Task ActualCleanup_PreservesDocumentLibraryFile_AndNeverDeletesNonDraft()
    {
        var now = new DateTime(2026, 10, 24, 8, 0, 0, DateTimeKind.Utc);
        var fileSystem = new RecordingFileSystem();
        var fixture = Fixture(dryRun: false, deletionConfirmed: true, fileSystem);
        Guid eligibleId;
        Guid submittedId;
        Guid documentId;
        await using (var db = fixture.Factory.CreateDbContext())
        {
            var eligible = DynamicDraft("PROTECTED", now.AddDays(-16));
            var submitted = DynamicDraft("NON-DRAFT", now.AddDays(-30));
            db.Entry(submitted).Property(x => x.Status).CurrentValue = VerificationApplicationStatus.Accepted;
            eligibleId = eligible.Id;
            submittedId = submitted.Id;
            var path = $"verification-applications/{eligible.Id:N}/formal.txt";
            var folder = Folder();
            var document = Document(folder.Id, path);
            documentId = document.Id;
            db.AddRange(eligible, submitted, folder, document, Attachment(eligible.Id, path, document.Id));
            await db.SaveChangesAsync();
        }

        await fixture.Worker.RunAsync(now);

        await using var verification = fixture.Factory.CreateDbContext();
        Assert.False(await verification.VerificationApplications.AnyAsync(x => x.Id == eligibleId));
        Assert.True(await verification.VerificationApplications.AnyAsync(x => x.Id == submittedId));
        Assert.True(await verification.DocumentLibraryFiles.AnyAsync(x => x.Id == documentId));
        Assert.Equal(0, fileSystem.Attempts);
    }

    [Fact]
    public async Task AttachmentUploadAndDelete_AdvanceEffectiveDraftUpdatedAt()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new AppDbContext(options);
        var root = Path.Combine(Path.GetTempPath(), "csit-draft-touch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var old = DateTime.UtcNow.AddDays(-20);
            var application = DynamicDraft("TOUCH", old);
            db.Add(application);
            await db.SaveChangesAsync();
            var service = new VerificationApplicationFileService(
                db,
                Options.Create(new DocumentStorageOptions { RootPath = Path.Combine(root, "library") }),
                Options.Create(new DocumentStagingOptions { RootPath = root }),
                new AllowBusinessWrite());
            var content = new MemoryStream("attachment"u8.ToArray());
            var formFile = new FormFile(content, 0, content.Length, "file", "touch.txt")
            {
                Headers = new HeaderDictionary(),
                ContentType = "text/plain"
            };

            var uploaded = await service.UploadAsync(application.Id, formFile, User("owner"));
            Assert.True(application.UpdatedAt > old);
            db.Entry(application).Property(x => x.UpdatedAt).CurrentValue = old;
            await db.SaveChangesAsync();

            Assert.True(await service.DeleteAsync(application.Id, uploaded.Id, User("owner")));
            Assert.True(application.UpdatedAt > old);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static TestFixture Fixture(
        bool dryRun,
        bool deletionConfirmed = false,
        RecordingFileSystem? fileSystem = null,
        bool usePhysicalFileSystem = false)
    {
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var factory = new TestDbContextFactory(dbOptions);
        var stateRoot = Path.Combine(Path.GetTempPath(), "csit-draft-cleanup-tests", Guid.NewGuid().ToString("N"));
        var cleanup = Options.Create(new VerificationDraftCleanupOptions
        {
            Enabled = true,
            DryRun = dryRun,
            DeletionConfirmed = deletionConfirmed,
            RetentionDays = 15,
            RunIntervalHours = 24,
            StatePath = Path.Combine(stateRoot, "pending.json")
        });
        var journal = new VerificationDraftCleanupJournal(cleanup);
        var worker = new VerificationDraftCleanupWorker(
            factory,
            cleanup,
            Options.Create(new DocumentStagingOptions { RootPath = stateRoot }),
            journal,
            usePhysicalFileSystem ? new VerificationDraftFileSystem() : fileSystem ?? new RecordingFileSystem(),
            NullLogger<VerificationDraftCleanupWorker>.Instance);
        return new TestFixture(factory, journal, worker, stateRoot);
    }

    private static VerificationApplication DynamicDraft(string number, DateTime updatedAt)
    {
        var application = LegacyDraft(number, updatedAt);
        application.SetValidationRequest(Guid.NewGuid(), 1, "ROUTER", "{}", updatedAt);
        return application;
    }

    private static VerificationApplication LegacyDraft(string number, DateTime updatedAt) =>
        VerificationApplication.CreateDraft(
            Guid.NewGuid(), "VA-" + number, Guid.NewGuid(), Guid.NewGuid(), "owner", "Owner",
            "owner@example.test", "RD", null,
            new VerificationApplicationContent { ProjectName = number }, updatedAt);

    private static VerificationApplicationFile Attachment(
        Guid applicationId,
        string path,
        Guid? documentLibraryFileId = null) => new()
    {
        Id = Guid.NewGuid(),
        VerificationApplicationId = applicationId,
        FileName = Path.GetFileName(path),
        FilePath = path,
        FileSize = 1,
        UploadedBy = "owner",
        CreatedAt = DateTime.UtcNow,
        DocumentLibraryFileId = documentLibraryFileId
    };

    private static DocumentFolder Folder() => new()
    {
        Id = Guid.NewGuid(), Level = 3, Name = "Formal", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };

    private static DocumentLibraryFile Document(Guid folderId, string path) => new()
    {
        Id = Guid.NewGuid(), FolderId = folderId, OriginalFileName = Path.GetFileName(path),
        StandardFileName = Path.GetFileName(path), UploadedBy = "owner", UploadedAt = DateTime.UtcNow,
        FileSize = 1, Sha256 = "hash", RelativePath = path, StorageArea = DocumentStorageAreas.Staging
    };

    private static ClaimsPrincipal User(string account) => new(new ClaimsIdentity(
        [new Claim("account", account), new Claim(ClaimTypes.NameIdentifier, account)], "test"));

    private sealed record TestFixture(
        TestDbContextFactory Factory,
        VerificationDraftCleanupJournal Journal,
        VerificationDraftCleanupWorker Worker,
        string StagingRoot);

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    private sealed class RecordingFileSystem : IVerificationDraftFileSystem
    {
        public int Attempts { get; private set; }
        public int FailuresRemaining { get; set; }

        public void DeleteStagingFile(string stagingRoot, string relativePath)
        {
            Attempts++;
            if (FailuresRemaining-- > 0) throw new IOException("simulated delete failure");
        }
    }

    private sealed class AllowBusinessWrite : IBusinessWriteAuthorizationGuard
    {
        public Task DemandAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
