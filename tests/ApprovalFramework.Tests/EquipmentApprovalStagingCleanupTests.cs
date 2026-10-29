using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Services.Approval;

namespace ApprovalFramework.Tests;

public sealed class EquipmentApprovalStagingCleanupTests
{
    private static readonly DateTime Now = new(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task CompletedEquipmentDeletionCleansOnlyItsCommittedStagingAndPreservesHistory()
    {
        var env = new Environment();
        try
        {
            var target = await env.AddSubmissionAsync("EQ-DELETED", ApprovalStatus.Committed,
                completedDeletion: true);
            var liveEquipment = await env.AddSubmissionAsync("EQ-LIVE", ApprovalStatus.Committed);
            var vendor = await env.AddSubmissionAsync("VENDOR-1", ApprovalStatus.Committed,
                module: "vendor", contextType: "Vendor", completedDeletion: true);

            Assert.Equal(1, await env.Service.CleanInactiveStagingBatchAsync());
            Assert.Equal(0, await env.Service.CleanInactiveStagingBatchAsync());

            await using var db = env.Factory.CreateDbContext();
            var cleaned = await db.PendingSubmissions.SingleAsync(x => x.Id == target.Id);
            Assert.Null(cleaned.StagingStorageKey);
            Assert.Equal(target.OriginalFileName, cleaned.OriginalFileName);
            Assert.Equal(target.FileHash, cleaned.FileHash);
            Assert.Equal(target.FileSize, cleaned.FileSize);
            Assert.Equal(target.CommitResultKey, cleaned.CommitResultKey);
            Assert.Equal(3, await db.ApprovalAuditEvents.CountAsync(x => x.SubmissionId == target.Id));
            Assert.NotNull((await db.PendingSubmissions.SingleAsync(x => x.Id == liveEquipment.Id))
                .StagingStorageKey);
            Assert.NotNull((await db.PendingSubmissions.SingleAsync(x => x.Id == vendor.Id))
                .StagingStorageKey);
            Assert.False(File.Exists(env.PathFor(target)));
            Assert.True(File.Exists(env.PathFor(liveEquipment)));
            Assert.True(File.Exists(env.PathFor(vendor)));
        }
        finally { env.Dispose(); }
    }

    [Fact]
    public async Task IncompleteAndNonCommittedFlowsKeepStaging()
    {
        var env = new Environment();
        try
        {
            var notDeleted = await env.AddSubmissionAsync("EQ-LIVE", ApprovalStatus.Committed);
            var pending = await env.AddSubmissionAsync("EQ-PENDING", ApprovalStatus.Pending,
                completedDeletion: true);
            var failed = await env.AddSubmissionAsync("EQ-FAILED", ApprovalStatus.CommitFailed,
                completedDeletion: true);

            Assert.Equal(0, await env.Service.CleanInactiveStagingBatchAsync());
            await using var db = env.Factory.CreateDbContext();
            foreach (var id in new[] { notDeleted.Id, pending.Id, failed.Id })
            {
                Assert.NotNull((await db.PendingSubmissions.SingleAsync(x => x.Id == id)).StagingStorageKey);
            }
        }
        finally { env.Dispose(); }
    }

    [Fact]
    public async Task MissingPhysicalStagingFileStillClearsMetadata()
    {
        var env = new Environment();
        try
        {
            var target = await env.AddSubmissionAsync("EQ-MISSING", ApprovalStatus.Committed,
                completedDeletion: true);
            File.Delete(env.PathFor(target));

            Assert.Equal(1, await env.Service.CleanInactiveStagingBatchAsync());
            await using var db = env.Factory.CreateDbContext();
            Assert.Null((await db.PendingSubmissions.SingleAsync(x => x.Id == target.Id)).StagingStorageKey);
            Assert.Equal(3, await db.ApprovalAuditEvents.CountAsync(x => x.SubmissionId == target.Id));
        }
        finally { env.Dispose(); }
    }

    [Fact]
    public async Task AccessDeniedKeepsCommittedStagingMetadataForRetry()
    {
        var env = new Environment(denyDelete: true);
        try
        {
            var target = await env.AddSubmissionAsync("EQ-DENIED", ApprovalStatus.Committed,
                completedDeletion: true);

            Assert.Equal(0, await env.Service.CleanInactiveStagingBatchAsync());
            await using var db = env.Factory.CreateDbContext();
            Assert.NotNull((await db.PendingSubmissions.SingleAsync(x => x.Id == target.Id))
                .StagingStorageKey);
            Assert.True(File.Exists(env.PathFor(target)));
        }
        finally { env.Dispose(); }
    }

    private sealed class Environment : IDisposable
    {
        public Factory Factory { get; } = new();
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "csit-equipment-staging-cleanup-" + Guid.NewGuid().ToString("N"));
        public ApprovalSubmissionService Service { get; }
        private readonly ApprovalStagingStorage storage;

        public Environment(bool denyDelete = false)
        {
            Directory.CreateDirectory(Root);
            var roots = new RootResolver(Root);
            storage = new ApprovalStagingStorage(roots);
            IApprovalStagingStorage serviceStorage = denyDelete
                ? new DeniedDeleteStorage(storage) : storage;
            var reviewerScope = new ApprovalReviewerScopeResolver(Factory, new DeniedAuthorization());
            Service = new ApprovalSubmissionService(Factory, reviewerScope,
                new ApprovalCommitHandlerRegistry([]), roots, serviceStorage);
        }

        public async Task<PendingSubmission> AddSubmissionAsync(string equipmentId,
            ApprovalStatus status, string module = "equipment", string contextType = "Asset",
            bool completedDeletion = false)
        {
            var id = Guid.NewGuid();
            await using var content = new MemoryStream("staged"u8.ToArray());
            var file = await storage.StoreAsync(id, content, "evidence.pdf", "application/pdf", 6);
            var submission = PendingSubmission.Create(id, id.ToString("N"), module, module,
                "engineer", "R&D", Guid.NewGuid(), "A", contextType, equipmentId,
                equipmentId, file, Now);
            if (status != ApprovalStatus.Pending)
            {
                submission.Approve("leader", Now.AddMinutes(1));
                var lease = Guid.NewGuid();
                submission.StartCommit(lease, Now.AddMinutes(2), TimeSpan.FromMinutes(5));
                if (status == ApprovalStatus.Committed)
                    submission.Commit(lease, id.ToString("N"), Now.AddMinutes(3));
                else if (status == ApprovalStatus.CommitFailed)
                    submission.FailCommit(lease, "test failure", Now.AddMinutes(3));
            }

            await using var db = Factory.CreateDbContext();
            db.PendingSubmissions.Add(submission);
            db.ApprovalAuditEvents.Add(ApprovalAuditEvent.Create(id, module, module,
                ApprovalAuditActions.Submitted, "engineer", Now, null, ApprovalStatus.Pending));
            if (status is ApprovalStatus.Committed or ApprovalStatus.CommitFailed)
                db.ApprovalAuditEvents.Add(ApprovalAuditEvent.Create(id, module, module,
                    ApprovalAuditActions.Approved, "leader", Now.AddMinutes(1),
                    ApprovalStatus.Pending, ApprovalStatus.ApprovedPendingCommit));
            if (status == ApprovalStatus.Committed)
                db.ApprovalAuditEvents.Add(ApprovalAuditEvent.Create(id, module, module,
                    ApprovalAuditActions.Committed, "ApprovalWorker", Now.AddMinutes(3),
                    ApprovalStatus.ApprovedPendingCommit, ApprovalStatus.Committed));
            if (completedDeletion)
            {
                var equipment = new Apparatus { Id = equipmentId, Name = equipmentId };
                var master = EquipmentDeletionJob.Create(equipment, 1, "admin", Now.AddMinutes(4));
                master.BeginFinalizing(Now.AddMinutes(5));
                master.MarkDbRecordDeleted(Now.AddMinutes(6));
                master.MarkCompleted(Now.AddMinutes(7));
                db.EquipmentDeletionJobs.Add(master);

                if (module == "equipment" && contextType == "Asset" && status == ApprovalStatus.Committed)
                {
                    var formal = new ApparatusFile
                    {
                        Id = id, ApparatusId = equipmentId, FileName = "evidence.pdf",
                        OriginalFileName = "evidence.pdf", FilePath = Root, FileSize = 6,
                        CreatedAt = Now
                    };
                    var fileJob = EquipmentFileDeletionJob.Create(formal, "admin", false,
                        Now.AddMinutes(4));
                    fileJob.BeginLocalCleanup(Now.AddMinutes(5));
                    fileJob.MarkLocalCleanupCompleted(true, Now.AddMinutes(6));
                    db.EquipmentFileDeletionJobs.Add(fileJob);
                }
            }
            await db.SaveChangesAsync();
            return submission;
        }

        public string PathFor(PendingSubmission submission) => System.IO.Path.Combine(Root,
            submission.StagingStorageKey!.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private sealed class Factory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> options =
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        public AppDbContext CreateDbContext() => new(options);
    }

    private sealed class RootResolver(string root) : IApprovalStagingRootResolver
    {
        public Task<string> GetRootAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(root);
    }

    private sealed class DeniedDeleteStorage(IApprovalStagingStorage inner) : IApprovalStagingStorage
    {
        public Task<StagedFileMetadata> StoreAsync(Guid submissionId, Stream content,
            string originalFileName, string contentType, long fileSize,
            CancellationToken cancellationToken = default) =>
            inner.StoreAsync(submissionId, content, originalFileName, contentType, fileSize,
                cancellationToken);
        public Task<Stream> OpenReadAsync(Guid submissionId, string storageKey,
            CancellationToken cancellationToken = default) =>
            inner.OpenReadAsync(submissionId, storageKey, cancellationToken);
        public Task DeleteAsync(Guid submissionId, string storageKey,
            CancellationToken cancellationToken = default) =>
            throw new UnauthorizedAccessException("test access denied");
    }

    private sealed class DeniedAuthorization : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource,
            IEnumerable<IAuthorizationRequirement> requirements) =>
            Task.FromResult(AuthorizationResult.Failed());
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource,
            string policyName) => Task.FromResult(AuthorizationResult.Failed());
    }
}
