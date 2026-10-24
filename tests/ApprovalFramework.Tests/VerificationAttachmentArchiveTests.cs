using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc;
using SIT.DepartmentSystem.Web.Controllers;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Models.Config;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Implementations;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace ApprovalFramework.Tests;

public sealed class VerificationAttachmentArchiveTests
{
    [Fact]
    public async Task AcceptedAttachment_IsVerifiedCopiedLinkedToMatchingThreeLevels_AndDownloadMatchesSha()
    {
        await using var fixture = await ArchiveFixture.CreateAsync();
        var application = await fixture.AddAcceptedAsync("MODEL-ARCHIVE", "approved-content");
        await fixture.Processor.EnqueueApprovedApplicationAsync(application, DateTime.UtcNow, CancellationToken.None);
        await using (var beforeArchive = fixture.Factory.CreateDbContext())
            Assert.Empty(await beforeArchive.KmIntegrationOutbox.ToListAsync());

        var result = await fixture.Processor.ProcessApplicationAsync(application.Id);

        Assert.Equal(1, result.Completed);
        Assert.Equal(0, result.Failed);
        await using var db = fixture.Factory.CreateDbContext();
        var attachment = await db.VerificationApplicationFiles.Include(x => x.DocumentLibraryFile)
            .SingleAsync(x => x.VerificationApplicationId == application.Id);
        var document = Assert.IsType<DocumentLibraryFile>(attachment.DocumentLibraryFile);
        Assert.Equal(DocumentStorageAreas.Library, document.StorageArea);
        var folders = await db.DocumentFolders.AsNoTracking().ToListAsync();
        var level1 = Assert.Single(folders, x => x.Level == 1);
        var level2 = Assert.Single(folders, x => x.Level == 2);
        var level3 = Assert.Single(folders, x => x.Level == 3);
        Assert.Equal(VerificationApplicationAttachmentFolders.Root, level1.Name);
        Assert.Equal("MODEL-ARCHIVE", level2.Name);
        Assert.Equal(level1.Id, level2.ParentId);
        Assert.Equal(VerificationApplicationAttachmentFolders.ApplicationDocuments, level3.Name);
        Assert.Equal(level2.Id, level3.ParentId);
        Assert.Equal(level3.Id, document.FolderId);
        Assert.StartsWith(Path.Combine(
            VerificationApplicationAttachmentFolders.Root, "MODEL-ARCHIVE",
            VerificationApplicationAttachmentFolders.ApplicationDocuments), document.RelativePath);
        Assert.False(File.Exists(Path.Combine(fixture.StagingRoot, attachment.FilePath)));
        var formalPath = Path.Combine(fixture.LibraryRoot, document.RelativePath);
        Assert.True(File.Exists(formalPath));
        Assert.Equal(document.Sha256, await ShaAsync(formalPath));
        var kmEvent = Assert.Single(await db.KmIntegrationOutbox.ToListAsync());
        Assert.Equal(KmIntegrationEventTypes.VerificationApplicationAttachmentArchived, kmEvent.EventType);
        Assert.Equal(KmIntegrationSourceEntityTypes.VerificationApplicationFile, kmEvent.SourceEntityType);
        Assert.Equal(attachment.Id, kmEvent.SourceEntityId);
        Assert.Equal(document.Id, kmEvent.DocumentId);
        Assert.Equal(1, kmEvent.Version);
        Assert.Null(kmEvent.ProjectId);
        Assert.Null(kmEvent.CaseId);
        Assert.Null(kmEvent.TaskId);
        Assert.Equal(KmIntegrationStatus.Pending, kmEvent.Status);

        var files = new VerificationApplicationFileService(
            db,
            Options.Create(new DocumentStorageOptions { RootPath = fixture.LibraryRoot }),
            Options.Create(new DocumentStagingOptions { RootPath = fixture.StagingRoot }),
            new AllowWrite(),
            fixture.Processor);
        await using var download = (await files.OpenDownloadAsync(
            attachment.Id, CsitUser()))!.Stream;
        Assert.Equal(document.Sha256, await ShaAsync(download));
        var dto = Assert.Single(await files.GetAsync(application.Id, CsitUser()));
        Assert.Equal(VerificationAttachmentArchiveStatuses.Completed, dto.FormalArchiveStatus);
        Assert.Equal(VerificationAttachmentKmStatuses.Disabled, dto.KmAnalysisStatus);
        Assert.Equal("KM 尚未啟用", dto.KmAnalysisStatusDisplay);
        Assert.Null(dto.FormalArchiveError);

        var kmDownload = new KmIntegrationController(
            fixture.Factory,
            NullLogger<KmIntegrationController>.Instance,
            Options.Create(new DocumentStorageOptions { RootPath = fixture.LibraryRoot }));
        var downloadResult = Assert.IsType<FileStreamResult>(
            await kmDownload.Download(document.Id, 1, CancellationToken.None));
        await using var kmStream = downloadResult.FileStream;
        Assert.Equal(document.Sha256, await ShaAsync(kmStream));
    }

    [Fact]
    public async Task SubmittedReturnedAndRejectedAttachments_RemainStagingWithoutFormalRecords()
    {
        await using var fixture = await ArchiveFixture.CreateAsync();
        var submitted = await fixture.AddSubmittedAsync("MODEL-SUBMITTED", "submitted");
        var returned = await fixture.AddSubmittedAsync("MODEL-RETURNED", "returned");
        var rejected = await fixture.AddSubmittedAsync("MODEL-REJECTED", "rejected");
        await using (var db = fixture.Factory.CreateDbContext())
        {
            var returnedEntity = await db.VerificationApplications.SingleAsync(x => x.Id == returned.Id);
            returnedEntity.Return("leader", "return", DateTime.UtcNow);
            var rejectedEntity = await db.VerificationApplications.SingleAsync(x => x.Id == rejected.Id);
            rejectedEntity.Reject("leader", "reject", DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        var result = await fixture.Processor.ProcessPendingAsync();

        Assert.Equal(0, result.Completed);
        await using var verification = fixture.Factory.CreateDbContext();
        Assert.Empty(await verification.DocumentFolders.ToListAsync());
        Assert.Empty(await verification.DocumentLibraryFiles.ToListAsync());
        Assert.Empty(await verification.KmIntegrationOutbox.ToListAsync());
        var files = await verification.VerificationApplicationFiles.ToListAsync();
        Assert.All(files, x => Assert.Null(x.DocumentLibraryFileId));
        Assert.All(files, x => Assert.True(File.Exists(Path.Combine(fixture.StagingRoot, x.FilePath))));
    }

    [Fact]
    public async Task TransferFailure_IsPersistedAsFailed_AndRestartRetriesWithoutDuplicates()
    {
        await using var fixture = await ArchiveFixture.CreateAsync();
        var application = await fixture.AddAcceptedAsync("MODEL-RETRY", "retry-content");
        await fixture.Processor.EnqueueApprovedApplicationAsync(application, DateTime.UtcNow, CancellationToken.None);
        Directory.Delete(fixture.LibraryRoot, recursive: true);
        await File.WriteAllTextAsync(fixture.LibraryRoot, "blocks directory creation");

        var first = await fixture.Processor.ProcessApplicationAsync(application.Id);

        Assert.Equal(1, first.Failed);
        await using (var failedDb = fixture.Factory.CreateDbContext())
        {
            Assert.Empty(await failedDb.DocumentLibraryFiles.ToListAsync());
            Assert.Empty(await failedDb.KmIntegrationOutbox.ToListAsync());
        }
        var pending = Assert.Single(await fixture.Journal.ReadAsync());
        Assert.Equal(1, pending.AttemptCount);
        Assert.NotNull(pending.LastError);
        File.Delete(fixture.LibraryRoot);
        Directory.CreateDirectory(fixture.LibraryRoot);
        var restartedJournal = new VerificationAttachmentArchiveJournal(
            Options.Create(new DocumentStagingOptions { RootPath = fixture.StagingRoot }));
        var restarted = fixture.CreateProcessor(restartedJournal);

        var second = await restarted.ProcessPendingAsync();
        await restarted.ProcessPendingAsync();

        Assert.Equal(1, second.Completed);
        Assert.Empty(await restartedJournal.ReadAsync());
        await using var db = fixture.Factory.CreateDbContext();
        Assert.Single(await db.DocumentLibraryFiles.ToListAsync());
        Assert.Single(await db.KmIntegrationOutbox.ToListAsync());
        Assert.Single(Directory.GetFiles(fixture.LibraryRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DatabaseFailure_LeavesVerifiedFormalFileAndSource_ForIdempotentRetry()
    {
        var interceptor = new FailFormalDocumentSaveInterceptor();
        await using var fixture = await ArchiveFixture.CreateAsync(interceptor);
        var application = await fixture.AddAcceptedAsync("MODEL-DB-RETRY", "database-retry");
        await fixture.Processor.EnqueueApprovedApplicationAsync(application, DateTime.UtcNow, CancellationToken.None);
        interceptor.FailuresRemaining = 1;

        var first = await fixture.Processor.ProcessApplicationAsync(application.Id);

        Assert.Equal(1, first.Failed);
        await using (var failedDb = fixture.Factory.CreateDbContext())
        {
            Assert.Empty(await failedDb.DocumentLibraryFiles.ToListAsync());
            Assert.Empty(await failedDb.KmIntegrationOutbox.ToListAsync());
            var attachment = await failedDb.VerificationApplicationFiles.SingleAsync();
            Assert.True(File.Exists(Path.Combine(fixture.StagingRoot, attachment.FilePath)));
        }
        Assert.Single(Directory.GetFiles(fixture.LibraryRoot, "*", SearchOption.AllDirectories));

        var second = await fixture.Processor.ProcessPendingAsync();

        Assert.Equal(1, second.Completed);
        await using var db = fixture.Factory.CreateDbContext();
        Assert.Single(await db.DocumentLibraryFiles.ToListAsync());
        Assert.Single(await db.KmIntegrationOutbox.ToListAsync());
        Assert.Empty(await fixture.Journal.ReadAsync());
    }

    [Fact]
    public async Task DuplicateEnqueueAndRepeatedProcessing_CreateOneFileAndOneLink()
    {
        await using var fixture = await ArchiveFixture.CreateAsync();
        var application = await fixture.AddAcceptedAsync("MODEL-IDEMPOTENT", "one-copy");
        await fixture.Processor.EnqueueApprovedApplicationAsync(application, DateTime.UtcNow, CancellationToken.None);
        await fixture.Processor.EnqueueApprovedApplicationAsync(application, DateTime.UtcNow, CancellationToken.None);

        await fixture.Processor.ProcessPendingAsync();
        await fixture.Processor.ProcessPendingAsync();

        await using var db = fixture.Factory.CreateDbContext();
        Assert.Single(await db.DocumentLibraryFiles.ToListAsync());
        Assert.Single(await db.KmIntegrationOutbox.ToListAsync());
        Assert.Single(Directory.GetFiles(fixture.LibraryRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task PreparedAttachmentEvent_IsDurableButNeverUsesTestReportV1Dispatch()
    {
        await using var fixture = await ArchiveFixture.CreateAsync();
        var application = await fixture.AddAcceptedAsync("MODEL-KM-PREPARED", "prepared");
        await fixture.Processor.EnqueueApprovedApplicationAsync(application, DateTime.UtcNow, CancellationToken.None);
        await fixture.Processor.ProcessPendingAsync();
        var client = new CountingKmClient();
        var enabled = new KmIntegrationOptions
        {
            Enabled = true, BaseUrl = "https://example.invalid", TimeoutSeconds = 2,
            MaxRetryCount = 3, RetryScheduleSeconds = [60], PollSeconds = 1, LeaseSeconds = 30
        };

        var processed = await new KmSyncWorker(
            fixture.Factory, client, Options.Create(enabled), NullLogger<KmSyncWorker>.Instance)
            .ProcessBatchAsync();

        Assert.Equal(0, processed);
        Assert.Equal(0, client.SubmitCount);
        await using var db = fixture.Factory.CreateDbContext();
        Assert.Equal(KmIntegrationStatus.Pending, Assert.Single(db.KmIntegrationOutbox).Status);
    }

    [Fact]
    public void KmPresentation_DoesNotTreatAcceptedDeliveryAsCompleted()
    {
        var departmentId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var application = VerificationApplication.CreateDraft(
            Guid.NewGuid(), "VA-KM-STATE", departmentId, teamId,
            "owner", "Owner", "owner@example.test", "RD", null,
            new VerificationApplicationContent
            {
                ProjectName = "MODEL", ProductModel = "MODEL", Customer = "CUSTOMER",
                ValidationRequirement = "Requirement",
                RequestedFinishDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7))
            }, DateTime.UtcNow);
        application.Submit(new VerificationApplicationRouting(
            departmentId, "D", "Department", teamId, "T", "Team", "verification", "leader", "Leader"),
            DateTime.UtcNow);
        application.Accept(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), "leader", DateTime.UtcNow);
        var attachment = new VerificationApplicationFile
        {
            Id = Guid.NewGuid(), VerificationApplicationId = application.Id,
            FileName = "attachment.txt", FileSize = 10, CreatedAt = DateTime.UtcNow,
            DocumentLibraryFileId = Guid.NewGuid()
        };
        attachment.DocumentLibraryFile = new DocumentLibraryFile
        {
            Id = attachment.DocumentLibraryFileId.Value, FolderId = Guid.NewGuid(),
            OriginalFileName = "attachment.txt", StandardFileName = "attachment.txt",
            FileSize = 10, StorageArea = DocumentStorageAreas.Library
        };
        var row = KmIntegrationOutbox.ForArchivedVerificationAttachment(application, attachment, DateTime.UtcNow);
        var lease = Guid.NewGuid();
        row.Claim(lease, DateTime.UtcNow, TimeSpan.FromMinutes(1));
        row.Sent(lease, "km-job-attachment-1", DateTime.UtcNow);
        var dto = new VerificationApplicationFileDto();

        VerificationAttachmentKmState.Apply(dto, true, row, true);

        Assert.Equal(VerificationAttachmentKmStatuses.WaitingAnalysis, dto.KmAnalysisStatus);
        Assert.Equal("KM 已接收，等待分析", dto.KmAnalysisStatusDisplay);
        Assert.NotEqual(VerificationAttachmentKmStatuses.Completed, dto.KmAnalysisStatus);
    }

    [Fact]
    public async Task AttachmentCallback_CompletesOnlyForMatchingJobDocumentAndVersion()
    {
        await using var fixture = await ArchiveFixture.CreateAsync();
        var application = await fixture.AddAcceptedAsync("MODEL-KM-CALLBACK", "callback");
        await fixture.Processor.EnqueueApprovedApplicationAsync(application, DateTime.UtcNow, CancellationToken.None);
        await fixture.Processor.ProcessPendingAsync();
        Guid documentId;
        await using (var db = fixture.Factory.CreateDbContext())
        {
            var row = Assert.Single(db.KmIntegrationOutbox);
            documentId = row.DocumentId;
            var lease = Guid.NewGuid();
            row.Claim(lease, DateTime.UtcNow, TimeSpan.FromMinutes(1));
            row.Sent(lease, "km-attachment-job", DateTime.UtcNow);
            await db.SaveChangesAsync();
        }
        var controller = new KmIntegrationController(
            fixture.Factory, NullLogger<KmIntegrationController>.Instance);
        var completedAt = DateTime.UtcNow;
        var mismatch = new KmStatusCallback(
            "km-attachment-job", "CSIT", Guid.NewGuid(), 1, "Completed",
            new KmCallbackTimestamps(null, completedAt, null), null);

        Assert.IsType<ConflictObjectResult>(await controller.Status(mismatch, default));
        await using (var unchanged = fixture.Factory.CreateDbContext())
            Assert.Equal(KmIntegrationStatus.Sent, Assert.Single(unchanged.KmIntegrationOutbox).Status);

        var matching = mismatch with { DocumentId = documentId };
        Assert.IsType<NoContentResult>(await controller.Status(matching, default));
        await using var verified = fixture.Factory.CreateDbContext();
        var rowAfterCallback = Assert.Single(verified.KmIntegrationOutbox);
        Assert.Equal(KmIntegrationStatus.Completed, rowAfterCallback.Status);
        var dto = new VerificationApplicationFileDto();
        VerificationAttachmentKmState.Apply(dto, true, rowAfterCallback, true);
        Assert.Equal(VerificationAttachmentKmStatuses.Completed, dto.KmAnalysisStatus);
    }

    [Fact]
    public async Task AttachmentKmFailure_IsShownPerFileWithoutChangingAcceptedApplication()
    {
        await using var fixture = await ArchiveFixture.CreateAsync();
        var application = await fixture.AddAcceptedAsync("MODEL-KM-FAILED", "failed");
        await fixture.Processor.EnqueueApprovedApplicationAsync(application, DateTime.UtcNow, CancellationToken.None);
        await fixture.Processor.ProcessPendingAsync();
        await using (var db = fixture.Factory.CreateDbContext())
        {
            var row = Assert.Single(db.KmIntegrationOutbox);
            var lease = Guid.NewGuid();
            row.Claim(lease, DateTime.UtcNow, TimeSpan.FromMinutes(1));
            row.Sent(lease, "km-attachment-failed", DateTime.UtcNow);
            row.CallbackFailed("KM_ANALYSIS_FAILED", "KM could not analyze the document.", DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        await using var readDb = fixture.Factory.CreateDbContext();
        var files = new VerificationApplicationFileService(
            readDb,
            Options.Create(new DocumentStorageOptions { RootPath = fixture.LibraryRoot }),
            Options.Create(new DocumentStagingOptions { RootPath = fixture.StagingRoot }),
            new AllowWrite(),
            fixture.Processor,
            Options.Create(new KmIntegrationOptions { Enabled = true }));
        var file = Assert.Single(await files.GetAsync(application.Id, CsitUser()));

        Assert.Equal(VerificationApplicationStatus.Accepted,
            (await readDb.VerificationApplications.SingleAsync(x => x.Id == application.Id)).Status);
        Assert.Equal(VerificationAttachmentKmStatuses.Failed, file.KmAnalysisStatus);
        Assert.Equal("KM could not analyze the document.", file.KmAnalysisError);
    }

    [Theory]
    [InlineData("..\\escape")]
    [InlineData("../escape")]
    [InlineData("CON")]
    [InlineData("model.")]
    public void ModelName_RejectsWindowsUnsafeAndTraversalValues(string modelName) =>
        Assert.Throws<InvalidOperationException>(() =>
            VerificationAttachmentArchiveProcessor.ValidateModelName(modelName));

    [Fact]
    public async Task ExistingWrongCollision_IsNeverOverwrittenOrLinked()
    {
        await using var fixture = await ArchiveFixture.CreateAsync();
        var application = await fixture.AddAcceptedAsync("MODEL-COLLISION", "expected");
        var attachment = application.Files.Single();
        var standard = DocumentLibraryRules.CreateStandardFileName(
            attachment.FileName, attachment.CreatedAt, attachment.Id);
        var path = Path.Combine(
            fixture.LibraryRoot,
            VerificationApplicationAttachmentFolders.Root,
            "MODEL-COLLISION",
            VerificationApplicationAttachmentFolders.ApplicationDocuments,
            standard);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "wrong");
        await fixture.Processor.EnqueueApprovedApplicationAsync(application, DateTime.UtcNow, CancellationToken.None);

        var result = await fixture.Processor.ProcessPendingAsync();

        Assert.Equal(1, result.Failed);
        Assert.Equal("wrong", await File.ReadAllTextAsync(path));
        await using var db = fixture.Factory.CreateDbContext();
        Assert.Empty(await db.DocumentLibraryFiles.ToListAsync());
    }

    [Fact]
    public async Task HistoricalAuditAndRepair_RequireConfirmation_AndNeverMoveStagingFile()
    {
        await using var fixture = await ArchiveFixture.CreateAsync();
        var application = await fixture.AddSubmittedAsync("MODEL-HISTORY", "historical");
        string sourcePath;
        await using (var db = fixture.Factory.CreateDbContext())
        {
            var attachment = await db.VerificationApplicationFiles.SingleAsync(
                x => x.VerificationApplicationId == application.Id);
            sourcePath = Path.Combine(fixture.StagingRoot, attachment.FilePath);
            var folder = new DocumentFolder
            {
                Id = Guid.NewGuid(), Level = 3, Name = "premature",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            };
            var document = new DocumentLibraryFile
            {
                Id = Guid.NewGuid(), FolderId = folder.Id, OriginalFileName = attachment.FileName,
                StandardFileName = Path.GetFileName(attachment.FilePath), UploadedBy = attachment.UploadedBy,
                UploadedAt = attachment.CreatedAt, FileSize = attachment.FileSize,
                Sha256 = attachment.Sha256!, RelativePath = attachment.FilePath,
                StorageArea = DocumentStorageAreas.Staging
            };
            db.AddRange(folder, document);
            attachment.DocumentLibraryFileId = document.Id;
            await db.SaveChangesAsync();
        }

        var report = await fixture.Processor.AuditHistoryAsync();
        Assert.Single(report.NonAcceptedWithDocumentLink);
        Assert.Empty(report.AcceptedNotInLibrary);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Processor.RemoveHistoricalPrematureLinksAsync([application.Id], false));

        Assert.Equal(1, await fixture.Processor.RemoveHistoricalPrematureLinksAsync([application.Id], true));
        Assert.True(File.Exists(sourcePath));
        await using var verification = fixture.Factory.CreateDbContext();
        Assert.Null((await verification.VerificationApplicationFiles.SingleAsync()).DocumentLibraryFileId);
        Assert.Empty(await verification.DocumentLibraryFiles.ToListAsync());
    }

    private static async Task<string> ShaAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return await ShaAsync(stream);
    }

    private static async Task<string> ShaAsync(Stream stream) =>
        Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();

    private static ClaimsPrincipal CsitUser() => new(new ClaimsIdentity([
        new Claim("account", "csit"),
        new Claim(ClaimTypes.NameIdentifier, "csit"),
        new Claim(SystemAuthorization.AccessScopeClaim, SystemAuthorization.AccessScopes.CsitStaff)
    ], "test"));

    private sealed class AllowWrite : IBusinessWriteAuthorizationGuard
    {
        public Task DemandAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class CountingKmClient : IKmIntegrationClient
    {
        public int SubmitCount { get; private set; }

        public Task<KmDeliveryResult> SubmitAsync(
            KmIngestionRequest request,
            CancellationToken cancellationToken)
        {
            SubmitCount++;
            return Task.FromResult(KmDeliveryResult.Success("unexpected"));
        }
    }

    private sealed class ArchiveFixture : IAsyncDisposable
    {
        private ArchiveFixture(
            string root,
            TestDbContextFactory factory,
            VerificationAttachmentArchiveJournal journal,
            VerificationAttachmentArchiveProcessor processor)
        {
            Root = root;
            Factory = factory;
            Journal = journal;
            Processor = processor;
        }

        public string Root { get; }
        public string StagingRoot => Path.Combine(Root, "staging");
        public string LibraryRoot => Path.Combine(Root, "library");
        public TestDbContextFactory Factory { get; }
        public VerificationAttachmentArchiveJournal Journal { get; }
        public VerificationAttachmentArchiveProcessor Processor { get; private set; }

        public static async Task<ArchiveFixture> CreateAsync(SaveChangesInterceptor? interceptor = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "csit-verification-archive-tests", Guid.NewGuid().ToString("N"));
            var staging = Path.Combine(root, "staging");
            var library = Path.Combine(root, "library");
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(library);
            var databaseRoot = new InMemoryDatabaseRoot();
            var builder = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"), databaseRoot);
            if (interceptor is not null) builder.AddInterceptors(interceptor);
            var factory = new TestDbContextFactory(builder.Options);
            await using (var db = factory.CreateDbContext()) await db.Database.EnsureCreatedAsync();
            var journal = new VerificationAttachmentArchiveJournal(
                Options.Create(new DocumentStagingOptions { RootPath = staging }));
            var fixture = new ArchiveFixture(root, factory, journal, null!);
            fixture.Processor = fixture.CreateProcessor(journal);
            return fixture;
        }

        public VerificationAttachmentArchiveProcessor CreateProcessor(
            VerificationAttachmentArchiveJournal journal) => new(
            Factory,
            Options.Create(new DocumentStorageOptions { RootPath = LibraryRoot }),
            Options.Create(new DocumentStagingOptions { RootPath = StagingRoot }),
            journal,
            NullLogger<VerificationAttachmentArchiveProcessor>.Instance);

        public Task<VerificationApplication> AddAcceptedAsync(string model, string content) =>
            AddAsync(model, content, accepted: true);

        public Task<VerificationApplication> AddSubmittedAsync(string model, string content) =>
            AddAsync(model, content, accepted: false);

        private async Task<VerificationApplication> AddAsync(string model, string content, bool accepted)
        {
            var now = DateTime.UtcNow;
            var departmentId = Guid.NewGuid();
            var teamId = Guid.NewGuid();
            var application = VerificationApplication.CreateDraft(
                Guid.NewGuid(), "VA-" + Guid.NewGuid().ToString("N")[..10], departmentId, teamId,
                "owner", "Owner", "owner@example.test", "RD", null,
                new VerificationApplicationContent
                {
                    ProjectName = model,
                    ProductModel = model,
                    Customer = "CUSTOMER",
                    RequestedFinishDate = DateOnly.FromDateTime(now.AddDays(7)),
                    ValidationRequirement = "Requirement"
                }, now);
            application.Submit(new VerificationApplicationRouting(
                departmentId, "DA40", "DA40", teamId, "TEAM", "Team", "verification", "leader", "Leader"), now);
            if (accepted)
                application.Accept(Guid.NewGuid(), DateOnly.FromDateTime(now.AddDays(7)), "leader", now);
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            var attachmentId = Guid.NewGuid();
            var standard = DocumentLibraryRules.CreateStandardFileName("attachment.txt", now, attachmentId);
            var relative = Path.Combine("verification-applications", application.Id.ToString("N"), standard);
            var path = Path.Combine(StagingRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes);
            application.Files.Add(new VerificationApplicationFile
            {
                Id = attachmentId,
                VerificationApplicationId = application.Id,
                FileName = "attachment.txt",
                FilePath = relative,
                ContentType = "text/plain",
                FileSize = bytes.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                UploadedBy = "owner",
                CreatedAt = now
            });
            await using var db = Factory.CreateDbContext();
            db.VerificationApplications.Add(application);
            await db.SaveChangesAsync();
            return application;
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    private sealed class FailFormalDocumentSaveInterceptor : SaveChangesInterceptor
    {
        public int FailuresRemaining { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (FailuresRemaining > 0 && eventData.Context!.ChangeTracker.Entries<DocumentLibraryFile>()
                    .Any(x => x.State is EntityState.Added or EntityState.Modified))
            {
                FailuresRemaining--;
                throw new DbUpdateException("simulated formal link commit failure");
            }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
