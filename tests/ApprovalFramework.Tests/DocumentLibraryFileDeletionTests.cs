using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Config;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Approval;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace ApprovalFramework.Tests;

public sealed class DocumentLibraryFileDeletionTests
{
    [Fact]
    public async Task NeverDispatchedFile_RemovesNasStagingAndDatabaseButKeepsAudit()
    {
        var setup = await SetupAsync();
        try
        {
            await WriteCopiesAsync(setup, nas: true, staging: true);
            await File.WriteAllBytesAsync(setup.ArchiveTemporaryPath, setup.Bytes);
            var service = Service(setup, kmDeletionEnabled: false);

            var result = await service.RequestAsync(setup.FileId, "admin");

            Assert.NotNull(result);
            Assert.True(result.Completed);
            Assert.Equal(DocumentLibraryFileDeletionStatuses.Completed, result.Status);
            await using var db = setup.Factory.CreateDbContext();
            Assert.False(await db.DocumentLibraryFiles.AnyAsync(x => x.Id == setup.FileId));
            Assert.False(await db.DocumentProcessingJobs.AnyAsync(x => x.DocumentFileId == setup.FileId));
            var audit = await db.DocumentLibraryFileDeletionJobs.SingleAsync(
                x => x.DocumentFileId == setup.FileId);
            Assert.Equal("admin", audit.RequestedBy);
            Assert.NotNull(audit.DbRecordsDeletedAt);
            Assert.NotNull(audit.NasFileRemovedAt);
            Assert.NotNull(audit.StagingFileRemovedAt);
            Assert.False(File.Exists(setup.NasPath));
            Assert.False(File.Exists(setup.StagingPath));
            Assert.False(File.Exists(setup.ArchiveTemporaryPath));

            var repeated = await service.RequestAsync(setup.FileId, "admin");
            Assert.Equal(result.RequestId, repeated!.RequestId);
            Assert.Single(await db.DocumentLibraryFileDeletionJobs.ToListAsync());
        }
        finally { setup.Dispose(); }
    }

    [Fact]
    public async Task CommittedApprovalStagingCopy_IsDeletedAndAuditMetadataIsCleared()
    {
        var setup = await SetupAsync();
        try
        {
            await WriteCopiesAsync(setup, nas: true, staging: false);
            var metadata = new StagedFileMetadata("managed.pdf", "application/pdf",
                setup.Bytes.Length,
                Convert.ToHexString(SHA256.HashData(setup.Bytes)), "approval/staged-file");
            var submission = PendingSubmission.Create(setup.FileId, setup.FileId.ToString("N"),
                ApprovalSourceMenuBinding.DocumentLibraryMenu,
                ApprovalSourceMenuBinding.DocumentLibraryModule, "employee", "Team A",
                Guid.NewGuid(), "TEAM-A", "DocumentLibraryFolder", Guid.NewGuid().ToString("N"),
                "Folder", metadata, DateTime.UtcNow);
            submission.Approve("leader", DateTime.UtcNow);
            var lease = Guid.NewGuid();
            submission.StartCommit(lease, DateTime.UtcNow, TimeSpan.FromMinutes(1));
            submission.Commit(lease, setup.FileId.ToString("N"), DateTime.UtcNow);
            await using (var db = setup.Factory.CreateDbContext())
            {
                db.PendingSubmissions.Add(submission);
                await db.SaveChangesAsync();
            }
            var approvalStaging = new RecordingApprovalStaging();

            var result = await Service(setup, false, approvalStaging)
                .RequestAsync(setup.FileId, "admin");

            Assert.True(result!.Completed);
            Assert.Equal((setup.FileId, "approval/staged-file"),
                Assert.Single(approvalStaging.Deleted));
            await using var verify = setup.Factory.CreateDbContext();
            Assert.Null((await verify.PendingSubmissions.SingleAsync()).StagingStorageKey);
            Assert.NotNull((await verify.DocumentLibraryFileDeletionJobs.SingleAsync())
                .ApprovalStagingRemovedAt);
        }
        finally { setup.Dispose(); }
    }

    [Fact]
    public async Task SentKmDocument_WaitsForVerifiedCallbackBeforeLocalCleanup()
    {
        var setup = await SetupAsync(withSentOutbox: true);
        try
        {
            await WriteCopiesAsync(setup, nas: true, staging: true);
            var service = Service(setup, kmDeletionEnabled: true);
            var requested = (await service.RequestAsync(setup.FileId, "leader"))!;
            Assert.Equal(DocumentLibraryFileDeletionStatuses.Pending, requested.Status);
            Assert.True(File.Exists(setup.NasPath));

            var client = new CapturingDeletionClient();
            var dispatcher = new KmDeletionDispatchService(setup.Factory, client,
                Options.Create(new KmDeletionOptions
                { Enabled = true, Scope = "mock.document.delete" }),
                Options.Create(new KmIntegrationOptions
                {
                    BaseUrl = "https://km.mock.test/", TimeoutSeconds = 2,
                    LeaseSeconds = 30, MaxRetryCount = 4
                }));
            Assert.Equal(1, await dispatcher.ProcessBatchAsync());
            var request = Assert.Single(client.Requests);
            Assert.Equal(setup.FileId, request.Resource.DocumentFileId);
            Assert.Equal("DocumentFileDeleted", request.ReasonCode);

            var callback = CompletedCallback(request, client.JobId);
            var callbacks = new KmDeletionCallbackService(setup.Factory);
            Assert.Equal(KmDeletionCallbackOutcome.Accepted,
                await callbacks.ApplyAsync(callback));
            Assert.Equal(KmDeletionCallbackOutcome.Accepted,
                await callbacks.ApplyAsync(callback));

            Assert.True(await service.TryAdvanceAsync(request.DeletionRequestId));
            await using var db = setup.Factory.CreateDbContext();
            Assert.False(await db.DocumentLibraryFiles.AnyAsync(x => x.Id == setup.FileId));
            var job = await db.DocumentLibraryFileDeletionJobs.SingleAsync();
            Assert.Equal(DocumentLibraryFileDeletionStatuses.Completed, job.Status);
            Assert.NotNull(job.KmVerifiedAt);
            Assert.Single(await db.DocumentLibraryFileDeletionCallbacks.ToListAsync());
            Assert.False(File.Exists(setup.NasPath));
            Assert.False(File.Exists(setup.StagingPath));
        }
        finally { setup.Dispose(); }
    }

    [Fact]
    public async Task KmDisabled_DoesNotPretendPreviouslySentDocumentWasDeleted()
    {
        var setup = await SetupAsync(withSentOutbox: true);
        try
        {
            await WriteCopiesAsync(setup, nas: true, staging: false);
            var result = (await Service(setup, kmDeletionEnabled: false)
                .RequestAsync(setup.FileId, "admin"))!;

            Assert.Equal(DocumentLibraryFileDeletionStatuses.Pending, result.Status);
            Assert.Equal("KM_DELETION_DISABLED", result.ErrorCode);
            Assert.False(result.Completed);
            await using var db = setup.Factory.CreateDbContext();
            Assert.True(await db.DocumentLibraryFiles.AnyAsync(x => x.Id == setup.FileId));
            Assert.True(await db.KmIntegrationOutbox.AnyAsync(x => x.DocumentId == setup.FileId));
            Assert.True(File.Exists(setup.NasPath));
        }
        finally { setup.Dispose(); }
    }

    [Fact]
    public async Task SourceOwnedDocument_IsRejectedWithoutCreatingDeletionJob()
    {
        var setup = await SetupAsync(linkToVerificationApplication: true);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Service(setup, false).RequestAsync(setup.FileId, "admin"));
            Assert.Contains("source lifecycle", error.Message);
            await using var db = setup.Factory.CreateDbContext();
            Assert.Empty(await db.DocumentLibraryFileDeletionJobs.ToListAsync());
            Assert.True(await db.DocumentLibraryFiles.AnyAsync(x => x.Id == setup.FileId));
        }
        finally { setup.Dispose(); }
    }

    [Fact]
    public async Task ActiveArchiveLease_HoldsDeletionAndPreventsEarlyPhysicalCleanup()
    {
        var setup = await SetupAsync(archiveInProgress: true);
        try
        {
            await WriteCopiesAsync(setup, nas: false, staging: true);
            var result = (await Service(setup, false)
                .RequestAsync(setup.FileId, "admin"))!;
            Assert.Equal(DocumentLibraryFileDeletionStatuses.Preparing, result.Status);
            Assert.Equal("ARCHIVE_IN_PROGRESS", result.ErrorCode);
            Assert.True(File.Exists(setup.StagingPath));
            await using var db = setup.Factory.CreateDbContext();
            Assert.True(await db.DocumentLibraryFiles.AnyAsync(x => x.Id == setup.FileId));
        }
        finally { setup.Dispose(); }
    }

    [Fact]
    public async Task ChecksumMismatch_BlocksCleanupAndKeepsFormalRecord()
    {
        var setup = await SetupAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(setup.NasPath)!);
            await File.WriteAllTextAsync(setup.NasPath, "tampered");
            var result = (await Service(setup, false)
                .RequestAsync(setup.FileId, "admin"))!;
            Assert.Equal(DocumentLibraryFileDeletionStatuses.CleanupFailed, result.Status);
            Assert.False(result.Completed);
            await using var db = setup.Factory.CreateDbContext();
            Assert.True(await db.DocumentLibraryFiles.AnyAsync(x => x.Id == setup.FileId));
            Assert.True(File.Exists(setup.NasPath));
        }
        finally { setup.Dispose(); }
    }

    [Fact]
    public async Task Authorization_AllowsAdminAndActiveLeaderOnly()
    {
        var factory = new Factory();
        var teamId = Guid.NewGuid();
        await using (var db = factory.CreateDbContext())
        {
            db.SystemOptions.Add(new SystemOption
            {
                Id = teamId, Category = SystemOptionCategories.Team, Name = "Team A",
                Value = "TEAM-A", IsEnabled = true
            });
            db.TeamRoutings.Add(new TeamRouting
            {
                Id = Guid.NewGuid(), TeamOptionId = teamId, LeaderAccount = "leader",
                LeaderDisplayName = "Leader", IsEnabled = true
            });
            await db.SaveChangesAsync();
        }
        var service = new DocumentLibraryDeletionAuthorizationService(factory,
            new AllowAuthorization());

        Assert.True(await service.CanDeleteAsync(User("admin", "Admin")));
        Assert.True(await service.CanDeleteAsync(User("leader")));
        Assert.False(await service.CanDeleteAsync(User("employee")));

        await using (var db = factory.CreateDbContext())
        {
            (await db.TeamRoutings.SingleAsync()).IsEnabled = false;
            await db.SaveChangesAsync();
        }
        Assert.False(await service.CanDeleteAsync(User("leader")));
    }

    [Fact]
    public void Migration_CreatesDurableJobsCallbacksAndNoForeignKeyToLiveDocument()
    {
        var root = FindRepositoryRoot();
        var migration = File.ReadAllText(Directory.GetFiles(Path.Combine(root, "Migrations"),
            "*AddDocumentLibraryFileDeletionLifecycle.cs").Single());
        Assert.Contains("document_file_deletion_jobs", migration);
        Assert.Contains("document_file_deletion_callbacks", migration);
        Assert.Contains("document_file_id", migration);
        Assert.DoesNotContain("principalTable: \"document_files\"", migration);
        Assert.Contains("onDelete: ReferentialAction.Restrict", migration);
    }

    private static DocumentLibraryFileDeletionService Service(Setup setup,
        bool kmDeletionEnabled, IApprovalStagingStorage? approvalStaging = null) => new(setup.Factory,
        Options.Create(new DocumentStorageOptions { RootPath = setup.NasRoot }),
        Options.Create(new DocumentStagingOptions { RootPath = setup.StagingRoot }),
        Options.Create(new KmDeletionOptions { Enabled = kmDeletionEnabled }),
        approvalStaging: approvalStaging);

    private static async Task<Setup> SetupAsync(bool withSentOutbox = false,
        bool linkToVerificationApplication = false, bool archiveInProgress = false)
    {
        var factory = new Factory();
        var nasRoot = NewRoot("nas");
        var stagingRoot = NewRoot("staging");
        var bytes = "document-delete-content"u8.ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var folder = new DocumentFolder
        {
            Id = Guid.NewGuid(), Level = 3, Name = "Managed documents",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        var file = new DocumentLibraryFile
        {
            Id = Guid.NewGuid(), FolderId = folder.Id, Folder = folder,
            OriginalFileName = "managed.pdf", StandardFileName = $"DOC-{Guid.NewGuid():N}.pdf",
            ContentType = "application/pdf", UploadedBy = "uploader", UploadedAt = DateTime.UtcNow,
            FileSize = bytes.Length, Sha256 = sha,
            RelativePath = Path.Combine("2026", "11", "managed-" + Guid.NewGuid().ToString("N") + ".pdf"),
            StorageArea = archiveInProgress ? DocumentStorageAreas.Staging : DocumentStorageAreas.Library
        };
        var job = new DocumentProcessingJob
        {
            Id = Guid.NewGuid(), DocumentFileId = file.Id, DocumentFile = file,
            Status = DocumentProcessingStatuses.Pending,
            ArchiveStatus = archiveInProgress
                ? DocumentArchiveStatuses.Archiving : DocumentArchiveStatuses.Completed,
            ArchiveLeaseToken = archiveInProgress ? Guid.NewGuid() : null,
            ArchiveLeaseExpiresAt = archiveInProgress ? DateTime.UtcNow.AddMinutes(5) : null,
            ArchiveSha256 = archiveInProgress ? null : sha,
            ArchivedAt = archiveInProgress ? null : DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        await using var db = factory.CreateDbContext();
        db.DocumentFolders.Add(folder);
        db.DocumentLibraryFiles.Add(file);
        db.DocumentProcessingJobs.Add(job);
        if (withSentOutbox)
        {
            var outbox = KmIntegrationOutbox.ForDocumentLibraryFile(file, job, DateTime.UtcNow);
            var lease = Guid.NewGuid();
            outbox.Claim(lease, DateTime.UtcNow, TimeSpan.FromMinutes(5));
            outbox.Sent(lease, "KM-INGEST-" + file.Id.ToString("N"), DateTime.UtcNow);
            db.KmIntegrationOutbox.Add(outbox);
        }
        if (linkToVerificationApplication)
        {
            var application = VerificationApplication.CreateDraft(Guid.NewGuid(), "VA-DELETE",
                null, null, "applicant", "Applicant", "applicant@example.com", "Team A", null,
                new VerificationApplicationContent { ProjectName = "Deletion source guard" },
                DateTime.UtcNow);
            db.VerificationApplications.Add(application);
            db.VerificationApplicationFiles.Add(new VerificationApplicationFile
            {
                Id = Guid.NewGuid(), VerificationApplicationId = application.Id,
                DocumentLibraryFileId = file.Id, FileName = file.OriginalFileName,
                FileSize = file.FileSize, UploadedBy = "applicant", CreatedAt = DateTime.UtcNow
            });
        }
        await db.SaveChangesAsync();
        return new Setup(factory, nasRoot, stagingRoot, file.Id, file.RelativePath, bytes);
    }

    private static async Task WriteCopiesAsync(Setup setup, bool nas, bool staging)
    {
        if (nas)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(setup.NasPath)!);
            await File.WriteAllBytesAsync(setup.NasPath, setup.Bytes);
        }
        if (staging)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(setup.StagingPath)!);
            await File.WriteAllBytesAsync(setup.StagingPath, setup.Bytes);
        }
    }

    private static KmDeletionCallback CompletedCallback(KmDeletionRequest request, string jobId)
    {
        var now = DateTimeOffset.UtcNow;
        return new KmDeletionCallback("1.0", Guid.NewGuid(), "CSIT", request.DeletionRequestId,
            jobId, request.Resource, "Completed",
            new KmDeletionTimestamps(now, now.AddSeconds(2), null),
            new KmDeletionResult(true, now.AddSeconds(1),
                new KmDeletionCleanup("Deleted", "Deleted", "Deleted", "Deleted",
                    "Deleted", "Deleted", "NotPresent")), null);
    }

    private static ClaimsPrincipal User(string account, string? role = null)
    {
        var claims = new List<Claim> { new("account", account) };
        if (role is not null) claims.Add(new Claim(ClaimTypes.Role, role));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static string NewRoot(string kind)
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"csit-document-delete-{kind}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class Setup(Factory factory, string nasRoot, string stagingRoot,
        Guid fileId, string relativePath, byte[] bytes) : IDisposable
    {
        public Factory Factory { get; } = factory;
        public string NasRoot { get; } = nasRoot;
        public string StagingRoot { get; } = stagingRoot;
        public Guid FileId { get; } = fileId;
        public byte[] Bytes { get; } = bytes;
        public string NasPath => Path.Combine(NasRoot, relativePath);
        public string StagingPath => Path.Combine(StagingRoot, relativePath);
        public string ArchiveTemporaryPath { get; } = Path.Combine(nasRoot, relativePath) +
            ".archive-" + Guid.NewGuid().ToString("N") + ".tmp";
        public void Dispose()
        {
            if (Directory.Exists(NasRoot)) Directory.Delete(NasRoot, true);
            if (Directory.Exists(StagingRoot)) Directory.Delete(StagingRoot, true);
        }
    }

    private sealed class RecordingApprovalStaging : IApprovalStagingStorage
    {
        public List<(Guid SubmissionId, string StorageKey)> Deleted { get; } = [];
        public Task<StagedFileMetadata> StoreAsync(Guid submissionId, Stream content,
            string originalFileName, string contentType, long fileSize,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(Guid submissionId, string storageKey,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(Guid submissionId, string storageKey,
            CancellationToken cancellationToken = default)
        {
            Deleted.Add((submissionId, storageKey));
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingDeletionClient : IKmDeletionClient
    {
        public List<KmDeletionRequest> Requests { get; } = [];
        public string JobId { get; } = "KM-DOCUMENT-DELETE";
        public Task<KmDeletionDeliveryResult> SubmitAsync(KmDeletionRequest request,
            CancellationToken cancellationToken = default)
        {
            Assert.True(request.IsValid());
            Requests.Add(request);
            return Task.FromResult(KmDeletionDeliveryResult.Success(JobId));
        }
    }

    private sealed class AllowAuthorization : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource,
            IEnumerable<IAuthorizationRequirement> requirements) =>
            Task.FromResult(AuthorizationResult.Success());

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource,
            string policyName) => Task.FromResult(AuthorizationResult.Success());
    }

    private sealed class Factory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> options =
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        public AppDbContext CreateDbContext() => new(options);
    }

    private static string FindRepositoryRoot()
    {
        var path = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(path, "SIT.DepartmentSystem.Web.csproj")))
            path = Directory.GetParent(path)?.FullName ?? throw new DirectoryNotFoundException();
        return path;
    }
}
