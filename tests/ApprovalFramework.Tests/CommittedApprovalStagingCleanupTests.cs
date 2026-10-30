using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Config;
using SIT.DepartmentSystem.Web.Services.Approval;

namespace ApprovalFramework.Tests;

public sealed class CommittedApprovalStagingCleanupTests
{
    private static readonly DateTime Now = new(2026, 10, 30, 8, 0, 0, DateTimeKind.Utc);

    public static TheoryData<string, ThreeLevelUploadKind?> SupportedFlows => new()
    {
        { "equipment", null },
        { "goods", null },
        { "certification", ThreeLevelUploadKind.CaseAttachment },
        { "verification", ThreeLevelUploadKind.CaseAttachment },
        { "verification", ThreeLevelUploadKind.TestReport }
    };

    [Theory]
    [MemberData(nameof(SupportedFlows))]
    public async Task VerifiedCommittedFormalFileCleansOnlyStagingAndPreservesHistory(
        string module, ThreeLevelUploadKind? kind)
    {
        using var env = new Environment();
        var submission = await env.AddAsync(ApprovalStatus.Committed, module, kind);

        Assert.Equal(1, await env.Service().CleanInactiveStagingBatchAsync());

        await using var db = env.Factory.CreateDbContext();
        var retained = await db.PendingSubmissions.SingleAsync(x => x.Id == submission.Id);
        Assert.Null(retained.StagingStorageKey);
        Assert.Equal(submission.CommitResultKey, retained.CommitResultKey);
        Assert.Equal(3, await db.ApprovalAuditEvents.CountAsync(x => x.SubmissionId == submission.Id));
        Assert.True(File.Exists(env.FormalPath(submission.Id)));
        Assert.True(File.Exists(env.RawMirrorPath(submission.Id)));
    }

    [Theory]
    [InlineData(ApprovalStatus.Pending)]
    [InlineData(ApprovalStatus.Rejected)]
    [InlineData(ApprovalStatus.CommitFailed)]
    public async Task IncompleteStatusesNeverCleanStaging(ApprovalStatus status)
    {
        using var env = new Environment();
        var submission = await env.AddAsync(status, "equipment", null);

        Assert.Equal(0, await env.Service().CleanInactiveStagingBatchAsync());

        await using var db = env.Factory.CreateDbContext();
        Assert.NotNull((await db.PendingSubmissions.SingleAsync(x => x.Id == submission.Id))
            .StagingStorageKey);
        Assert.True(File.Exists(env.StagingPath(submission)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingOrChangedFormalFileRefusesCleanup(bool deleteFormal)
    {
        using var env = new Environment();
        var submission = await env.AddAsync(ApprovalStatus.Committed, "equipment", null);
        if (deleteFormal) File.Delete(env.FormalPath(submission.Id));
        else await File.WriteAllBytesAsync(env.FormalPath(submission.Id), "damage"u8.ToArray());

        Assert.Equal(0, await env.Service().CleanInactiveStagingBatchAsync());

        await using var db = env.Factory.CreateDbContext();
        Assert.NotNull((await db.PendingSubmissions.SingleAsync(x => x.Id == submission.Id))
            .StagingStorageKey);
    }

    [Fact]
    public async Task DeleteFailureKeepsKeyAndExistingBackgroundBatchCanRetry()
    {
        using var env = new Environment();
        var submission = await env.AddAsync(ApprovalStatus.Committed, "equipment", null);

        Assert.Equal(0, await env.Service(denyDelete: true).CleanInactiveStagingBatchAsync());
        Assert.Equal(1, await env.Service().CleanInactiveStagingBatchAsync());

        await using var db = env.Factory.CreateDbContext();
        Assert.Null((await db.PendingSubmissions.SingleAsync(x => x.Id == submission.Id))
            .StagingStorageKey);
    }

    [Fact]
    public async Task NewCleanupDoesNotTouchAnotherOrHistoricalSubmission()
    {
        using var env = new Environment();
        var historical = await env.AddAsync(ApprovalStatus.Committed, "equipment", null,
            Now.AddDays(-2));
        var current = await env.AddAsync(ApprovalStatus.Committed, "equipment", null, Now);

        Assert.Equal(1, await env.Service().CleanInactiveStagingBatchAsync());

        await using var db = env.Factory.CreateDbContext();
        Assert.NotNull((await db.PendingSubmissions.SingleAsync(x => x.Id == historical.Id))
            .StagingStorageKey);
        Assert.Null((await db.PendingSubmissions.SingleAsync(x => x.Id == current.Id))
            .StagingStorageKey);
        Assert.True(File.Exists(env.StagingPath(historical)));
    }

    [Fact]
    public async Task DefaultOptionsDoNotBackfillCommittedHistory()
    {
        using var env = new Environment();
        var historical = await env.AddAsync(ApprovalStatus.Committed, "equipment", null,
            Now.AddDays(-2));

        Assert.Equal(0, await env.Service(defaultOptions: true).CleanInactiveStagingBatchAsync());

        await using var db = env.Factory.CreateDbContext();
        Assert.NotNull((await db.PendingSubmissions.SingleAsync(x => x.Id == historical.Id))
            .StagingStorageKey);
    }

    [Fact]
    public async Task MissingStagingIsIdempotentlyClearedOnlyAfterFormalVerification()
    {
        using var env = new Environment();
        var submission = await env.AddAsync(ApprovalStatus.Committed, "equipment", null);
        File.Delete(env.StagingPath(submission));

        Assert.Equal(1, await env.Service().CleanInactiveStagingBatchAsync());

        await using var db = env.Factory.CreateDbContext();
        Assert.Null((await db.PendingSubmissions.SingleAsync(x => x.Id == submission.Id))
            .StagingStorageKey);
    }

    [Fact]
    public async Task DryRunClassifiesWithoutChangingSubmissionOrAuditHistory()
    {
        using var env = new Environment();
        var safe = await env.AddAsync(ApprovalStatus.Committed, "equipment", null);
        var mismatch = await env.AddAsync(ApprovalStatus.Committed, "equipment", null);
        var stagingMissing = await env.AddAsync(ApprovalStatus.Committed, "equipment", null);
        var auditMissing = await env.AddAsync(ApprovalStatus.Committed, "equipment", null,
            includeCommittedAudit: false);
        await File.WriteAllBytesAsync(env.FormalPath(mismatch.Id), "tamper-content"u8.ToArray());
        File.Delete(env.StagingPath(stagingMissing));

        var result = await env.Service(defaultOptions: true).DryRunCommittedStagingAsync();

        Assert.Equal(4, result.Total);
        Assert.Equal(1, result.Counts[CommittedStagingDryRunCategory.SafeToClean]);
        Assert.Equal(1, result.Counts[CommittedStagingDryRunCategory.FileMismatch]);
        Assert.Equal(1, result.Counts[CommittedStagingDryRunCategory.StagingMissing]);
        Assert.Equal(1, result.Counts[CommittedStagingDryRunCategory.CommitOrAuditIncomplete]);
        await using var verify = env.Factory.CreateDbContext();
        Assert.Equal(4, await verify.PendingSubmissions.CountAsync(x => x.StagingStorageKey != null));
        Assert.Equal(11, await verify.ApprovalAuditEvents.CountAsync());
        Assert.True(File.Exists(env.StagingPath(safe)));
    }

    private sealed class Environment : IDisposable
    {
        public Factory Factory { get; } = new();
        public string Root { get; } = Path.Combine(Path.GetTempPath(),
            "csit-committed-staging-cleanup-" + Guid.NewGuid().ToString("N"));
        private readonly RootResolver roots;
        private readonly ApprovalStagingStorage storage;

        public Environment()
        {
            Directory.CreateDirectory(Root);
            roots = new RootResolver(Path.Combine(Root, "staging"));
            Directory.CreateDirectory(roots.Root);
            storage = new ApprovalStagingStorage(roots);
        }

        public ApprovalSubmissionService Service(bool denyDelete = false,
            bool defaultOptions = false)
        {
            IApprovalStagingStorage selected = denyDelete
                ? new DeniedDeleteStorage(storage) : storage;
            var options = defaultOptions ? new ApprovalStagingCleanupOptions() : new()
            {
                EnableNewSubmissionCleanup = true,
                NewSubmissionNotBeforeUtc = Now.AddHours(-1),
                EnableHistoricalCleanup = false,
                BatchSize = 20
            };
            return new ApprovalSubmissionService(Factory,
                new ApprovalReviewerScopeResolver(Factory, new DeniedAuthorization()),
                new ApprovalCommitHandlerRegistry([]), roots, selected, null, Options.Create(options));
        }

        public async Task<PendingSubmission> AddAsync(ApprovalStatus status, string module,
            ThreeLevelUploadKind? kind, DateTime? submittedAt = null,
            bool includeCommittedAudit = true)
        {
            var at = submittedAt ?? Now;
            var id = Guid.NewGuid();
            var bytes = "formal-content"u8.ToArray();
            await using var content = new MemoryStream(bytes);
            var staged = await storage.StoreAsync(id, content, "evidence.bin",
                "application/octet-stream", bytes.Length);
            var contextId = "ASSET-" + id.ToString("N");
            var contextType = "Asset";
            Guid? contextGuid = null;
            if (kind is not null)
            {
                contextGuid = Guid.NewGuid();
                contextType = kind.Value.ToString();
                contextId = contextGuid.Value.ToString("N");
            }
            var submission = PendingSubmission.Create(id, id.ToString("N"), module, module,
                "engineer", "R&D", Guid.NewGuid(), "A", contextType, contextId,
                contextId, staged, at);
            if (status != ApprovalStatus.Pending && status != ApprovalStatus.Rejected)
            {
                submission.Approve("leader", at.AddMinutes(1));
                var lease = Guid.NewGuid();
                submission.StartCommit(lease, at.AddMinutes(2), TimeSpan.FromMinutes(5));
                if (status == ApprovalStatus.Committed)
                    submission.Commit(lease, id.ToString("N"), at.AddMinutes(3));
                else submission.FailCommit(lease, "test failure", at.AddMinutes(3));
            }
            else if (status == ApprovalStatus.Rejected)
                submission.Reject("leader", "not approved", at.AddMinutes(1));

            Directory.CreateDirectory(Path.GetDirectoryName(FormalPath(id))!);
            Directory.CreateDirectory(Path.GetDirectoryName(RawMirrorPath(id))!);
            await File.WriteAllBytesAsync(FormalPath(id), bytes);
            await File.WriteAllBytesAsync(RawMirrorPath(id), bytes);
            await File.WriteAllTextAsync(RawManifestPath(id), "{}");

            await using var db = Factory.CreateDbContext();
            db.PendingSubmissions.Add(submission);
            db.ApprovalAuditEvents.Add(ApprovalAuditEvent.Create(id, module, module,
                ApprovalAuditActions.Submitted, "engineer", at, null, ApprovalStatus.Pending));
            if (status is ApprovalStatus.Committed or ApprovalStatus.CommitFailed)
                db.ApprovalAuditEvents.Add(ApprovalAuditEvent.Create(id, module, module,
                    ApprovalAuditActions.Approved, "leader", at.AddMinutes(1),
                    ApprovalStatus.Pending, ApprovalStatus.ApprovedPendingCommit));
            if (status == ApprovalStatus.Committed && includeCommittedAudit)
                db.ApprovalAuditEvents.Add(ApprovalAuditEvent.Create(id, module, module,
                    ApprovalAuditActions.Committed, "approvalworker", at.AddMinutes(3),
                    ApprovalStatus.ApprovedPendingCommit, ApprovalStatus.Committed));
            if (kind is null)
                SeedAsset(db, submission, bytes.Length);
            else
                SeedThreeLevel(db, submission, kind.Value, contextGuid!.Value, bytes.Length);
            await db.SaveChangesAsync();
            return submission;
        }

        private void SeedAsset(AppDbContext db, PendingSubmission submission, long size)
        {
            var asset = new Apparatus
            {
                Id = submission.ContextId, Name = submission.ContextId,
                ModuleCode = submission.SourceModuleCode
            };
            db.Apparatuses.Add(asset);
            db.ApparatusFiles.Add(new ApparatusFile
            {
                Id = submission.Id, ApparatusId = asset.Id, FileName = "evidence.bin",
                FilePath = Path.GetDirectoryName(FormalPath(submission.Id))!, FileSize = size,
                Sha256 = submission.FileHash, IsRawDataExported = true,
                NasFolderPath = Path.Combine(Root, "raw", submission.Id.ToString("N")),
                RawJsonPath = RawManifestPath(submission.Id), CreatedAt = Now
            });
        }

        private void SeedThreeLevel(AppDbContext db, PendingSubmission submission,
            ThreeLevelUploadKind kind, Guid contextId, long size)
        {
            var module = new ModuleEntity
            {
                Id = Guid.NewGuid(), Code = submission.SourceModuleCode,
                Name = submission.SourceModuleCode, DisplayName = submission.SourceModuleCode
            };
            var record = new ModuleRecord
            {
                Id = Guid.NewGuid(), ModuleId = module.Id, Module = module,
                RecordNo = "R", Name = "Record"
            };
            Guid? caseId = null;
            Guid? taskId = null;
            if (kind == ThreeLevelUploadKind.CaseAttachment)
            {
                caseId = contextId;
                db.ModuleRecordCases.Add(new ModuleRecordCase
                {
                    Id = contextId, RecordId = record.Id, Record = record,
                    CaseNo = "C", Name = "Case"
                });
            }
            else
            {
                caseId = Guid.NewGuid();
                taskId = contextId;
                var recordCase = new ModuleRecordCase
                {
                    Id = caseId.Value, RecordId = record.Id, Record = record,
                    CaseNo = "C", Name = "Case"
                };
                db.ModuleRecordCases.Add(recordCase);
                db.ModuleRecordTasks.Add(new ModuleRecordTask
                {
                    Id = taskId.Value, CaseId = caseId.Value, Case = recordCase,
                    TaskNo = "T", Name = "Task"
                });
            }
            db.ModuleCaseFiles.Add(new ModuleCaseFile
            {
                Id = submission.Id, RecordId = record.Id, Record = record,
                CaseId = caseId, CaseNo = "C", TaskId = taskId, TaskNo = taskId is null ? null : "T",
                FileName = "evidence.bin", FilePath = Path.GetDirectoryName(FormalPath(submission.Id))!,
                FileSize = size, Sha256 = submission.FileHash, DocumentId = Guid.NewGuid(), VersionNo = 1,
                FileKind = kind == ThreeLevelUploadKind.TestReport
                    ? ModuleCaseFileKinds.TestReport : ModuleCaseFileKinds.Attachment,
                ReviewStatus = kind == ThreeLevelUploadKind.TestReport
                    ? TestReportReviewStatuses.Approved : null,
                IsRawDataExported = true, NasFilePath = RawMirrorPath(submission.Id),
                RawJsonPath = RawManifestPath(submission.Id), CreatedAt = Now, UpdatedAt = Now
            });
        }

        public string FormalPath(Guid id) => Path.Combine(Root, "formal", id.ToString("N"), "evidence.bin");
        public string RawMirrorPath(Guid id) => Path.Combine(Root, "raw", id.ToString("N"),
            id.ToString("N"), "evidence.bin");
        private string RawManifestPath(Guid id) => Path.Combine(Root, "raw", id.ToString("N"),
            "rawdata.json");
        public string StagingPath(PendingSubmission item) => Path.Combine(roots.Root,
            item.StagingStorageKey!.Replace('/', Path.DirectorySeparatorChar));

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
        public string Root { get; } = root;
        public Task<string> GetRootAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Root);
    }

    private sealed class DeniedDeleteStorage(IApprovalStagingStorage inner) : IApprovalStagingStorage
    {
        public Task<StagedFileMetadata> StoreAsync(Guid submissionId, Stream content,
            string originalFileName, string contentType, long fileSize,
            CancellationToken cancellationToken = default) => inner.StoreAsync(submissionId,
                content, originalFileName, contentType, fileSize, cancellationToken);
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
