using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models;
using SIT.DepartmentSystem.Web.Models.Config;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Approval;
using SIT.DepartmentSystem.Web.Services.Implementations;
using SIT.DepartmentSystem.Web.Services.Interfaces;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace ApprovalFramework.Tests;

public sealed class DocumentLibraryApprovalLifecycleTests
{
    [Fact]
    public async Task AdminAndActiveTeamLeader_BypassWithAudit_ArchiveBeforeKmEvenWhenKmDisabled()
    {
        await using var env = await Environment.CreateAsync();
        var admin = Principal("admin", "Admin", "Administration");
        var leader = Principal("leader", "User", "Team A");

        var adminResult = await env.Upload.UploadAsync(env.CustomFolderId,
            File("admin.txt", "admin-content"), admin);
        var leaderResult = await env.Upload.UploadAsync(env.CustomFolderId,
            File("leader.txt", "leader-content"), leader);

        Assert.Equal(ApprovalUploadDecisionKind.ApprovalBypassed, adminResult.Kind);
        Assert.Equal(ApprovalUploadDecisionKind.ApprovalBypassed, leaderResult.Kind);
        Assert.Null(adminResult.SubmissionId);
        Assert.Null(leaderResult.SubmissionId);
        await using (var db = await env.Factory.CreateDbContextAsync())
        {
            Assert.Empty(db.PendingSubmissions);
            var audits = await db.ApprovalAuditEvents.OrderBy(x => x.OccurredAt).ToListAsync();
            Assert.Equal(2, audits.Count);
            Assert.Contains(audits, x => x.Details == "Basis=Administration");
            Assert.Contains(audits, x => x.Details == "Basis=ActiveTeamLeader");
            Assert.Empty(db.KmIntegrationOutbox);
        }

        Assert.Equal(2, await env.Archive.ProcessBatchAsync());
        Assert.Equal(0, await env.Archive.ProcessBatchAsync());
        await using (var db = await env.Factory.CreateDbContextAsync())
        {
            Assert.Equal(2, await db.DocumentLibraryFiles.CountAsync(x =>
                x.StorageArea == DocumentStorageAreas.Library));
            Assert.Equal(2, await db.DocumentProcessingJobs.CountAsync(x =>
                x.ArchiveStatus == DocumentArchiveStatuses.Completed));
            Assert.Equal(2, await db.KmIntegrationOutbox.CountAsync());
            Assert.All(await db.KmIntegrationOutbox.ToListAsync(),
                x => Assert.Equal(KmIntegrationStatus.Pending, x.Status));
        }
    }

    [Fact]
    public async Task Employee_StagesForSupervisor_ApproveCommits_RejectNeverArchives_AndRetryIsIdempotent()
    {
        await using var env = await Environment.CreateAsync();
        var employee = Principal("employee", "User", "Team A");
        var leader = Principal("leader", "User", "Team A");

        var pending = await env.Upload.UploadAsync(env.CustomFolderId,
            File("pending.pdf", "approved-content"), employee);
        Assert.Equal(ApprovalUploadDecisionKind.Pending, pending.Kind);
        Assert.NotNull(pending.SubmissionId);
        Assert.Null(pending.File);
        Assert.Contains(await env.Submissions.ListActiveSourcesAsync(),
            x => x.Code == ApprovalSourceMenuBinding.DocumentLibraryMenu);
        await using (var db = await env.Factory.CreateDbContextAsync())
        {
            Assert.Empty(db.DocumentLibraryFiles);
            Assert.Empty(db.KmIntegrationOutbox);
            var row = await db.PendingSubmissions.SingleAsync(x => x.Id == pending.SubmissionId);
            Assert.Equal(ApprovalStatus.Pending, row.Status);
            Assert.Equal("employee", row.SubmittedByAccount);
            Assert.Equal("TEAM-A", row.SubmittedTeamCode);
        }

        var rejected = await env.Upload.UploadAsync(env.ProductFolderId,
            File("rejected.txt", "rejected-content"), employee);
        await env.Submissions.RejectAsync(rejected.SubmissionId!.Value, leader, "needs correction");
        Assert.Equal(0, await env.Archive.ProcessBatchAsync());

        await env.Submissions.ApproveAsync(pending.SubmissionId!.Value, leader);
        await env.Commit.ProcessAsync(pending.SubmissionId.Value);
        await env.Commit.ProcessAsync(pending.SubmissionId.Value);
        await using (var db = await env.Factory.CreateDbContextAsync())
        {
            Assert.Equal(ApprovalStatus.Committed,
                (await db.PendingSubmissions.SingleAsync(x => x.Id == pending.SubmissionId)).Status);
            Assert.Single(db.DocumentLibraryFiles);
            Assert.Empty(db.KmIntegrationOutbox);
        }

        Assert.Equal(1, await env.Archive.ProcessBatchAsync());
        Assert.Equal(0, await env.Archive.ProcessBatchAsync());
        await using (var db = await env.Factory.CreateDbContextAsync())
        {
            var file = await db.DocumentLibraryFiles.SingleAsync();
            var job = await db.DocumentProcessingJobs.SingleAsync();
            Assert.Equal(DocumentStorageAreas.Library, file.StorageArea);
            Assert.Equal(DocumentArchiveStatuses.Completed, job.ArchiveStatus);
            Assert.Single(db.KmIntegrationOutbox);
            Assert.False(await db.DocumentLibraryFiles.AnyAsync(x => x.Id == rejected.SubmissionId));
            Assert.False(await db.KmIntegrationOutbox.AnyAsync(x => x.DocumentId == rejected.SubmissionId));

            job.DeliveryAccepted(DateTime.UtcNow);
            job.CallbackFailed(DateTime.UtcNow, DateTime.UtcNow, "KM_FAILED",
                "Synthetic KM failure.", false, DateTime.UtcNow);
            await db.SaveChangesAsync();
        }
        await using (var db = await env.Factory.CreateDbContextAsync())
        {
            Assert.Equal(DocumentStorageAreas.Library,
                (await db.DocumentLibraryFiles.SingleAsync()).StorageArea);
            Assert.Equal(DocumentArchiveStatuses.Completed,
                (await db.DocumentProcessingJobs.SingleAsync()).ArchiveStatus);
        }
    }

    [Fact]
    public async Task MissingRoutingAndLegacyFilesWithoutEvidence_FailClosed()
    {
        await using var env = await Environment.CreateAsync();
        var unrouteable = Principal("unrouteable", "User", "Unknown Department");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            env.Upload.UploadAsync(env.CustomFolderId, File("blocked.txt", "blocked"), unrouteable));
        Assert.Contains("無法由 Shared Identity 部門唯一判定", error.Message);

        var legacy = await env.Formal.StageApprovedAsync(Guid.NewGuid(), env.CustomFolderId,
            File("legacy.txt", "legacy-content"), "legacy");
        Assert.Equal(0, await env.Archive.ProcessBatchAsync());
        await using var db = await env.Factory.CreateDbContextAsync();
        Assert.Equal(DocumentStorageAreas.Staging,
            (await db.DocumentLibraryFiles.SingleAsync(x => x.Id == legacy.Id)).StorageArea);
        Assert.Empty(db.KmIntegrationOutbox);
        Assert.Empty(db.PendingSubmissions);
    }

    [Fact]
    public async Task ArchiveStatusBatch_UsesFileIds_MapsTrueState_AndDistinguishesRetryableFailure()
    {
        await using var env = await Environment.CreateAsync();
        var admin = Principal("admin", "Admin", "Administration");
        var first = (await env.Upload.UploadAsync(env.CustomFolderId,
            File("same.txt", "first"), admin)).File!;
        var second = (await env.Upload.UploadAsync(env.CustomFolderId,
            File("same.txt", "second"), admin)).File!;
        var third = (await env.Upload.UploadAsync(env.CustomFolderId,
            File("third.txt", "third"), admin)).File!;

        var pending = Assert.Single(await env.Formal.GetArchiveStatusesAsync([first.Id]));
        Assert.Equal(DocumentArchiveStatuses.Pending, pending.ArchiveStatus);
        Assert.Equal("待正式入庫", pending.ArchiveStatusDisplay);
        Assert.True(pending.ContinueTracking);

        await using (var db = await env.Factory.CreateDbContextAsync())
        {
            var jobs = await db.DocumentProcessingJobs.ToDictionaryAsync(x => x.DocumentFileId);
            jobs[first.Id].ArchiveStatus = DocumentArchiveStatuses.Completed;
            jobs[first.Id].ArchiveLastError = null;
            jobs[second.Id].ArchiveStatus = DocumentArchiveStatuses.Failed;
            jobs[second.Id].ArchiveLastError = "NAS unavailable";
            jobs[second.Id].ArchiveNextRetryAt = DateTime.UtcNow.AddMinutes(1);
            jobs[third.Id].ArchiveStatus = DocumentArchiveStatuses.Archiving;
            await db.SaveChangesAsync();
        }

        var rows = await env.Formal.GetArchiveStatusesAsync(
            [first.Id, second.Id, third.Id, second.Id, Guid.NewGuid()]);

        Assert.Equal(3, rows.Count);
        var completed = Assert.Single(rows, x => x.FileId == first.Id);
        Assert.Equal("正式入庫完成", completed.ArchiveStatusDisplay);
        Assert.Equal("success", completed.ArchiveStatusTone);
        Assert.False(completed.ContinueTracking);
        var failed = Assert.Single(rows, x => x.FileId == second.Id);
        Assert.Equal("寫入失敗", failed.ArchiveStatusDisplay);
        Assert.Equal("danger", failed.ArchiveStatusTone);
        Assert.Equal("NAS unavailable", failed.ArchiveError);
        Assert.True(failed.ContinueTracking);
        var archiving = Assert.Single(rows, x => x.FileId == third.Id);
        Assert.Equal("正式入庫中", archiving.ArchiveStatusDisplay);
        Assert.True(archiving.ContinueTracking);

        await using (var db = await env.Factory.CreateDbContextAsync())
        {
            var job = await db.DocumentProcessingJobs.SingleAsync(x => x.DocumentFileId == second.Id);
            job.ArchiveNextRetryAt = null;
            await db.SaveChangesAsync();
        }
        failed = Assert.Single(await env.Formal.GetArchiveStatusesAsync([second.Id]));
        Assert.False(failed.ContinueTracking);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            env.Formal.GetArchiveStatusesAsync(Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToArray()));
    }

    private static FormFile File(string name, string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "files", name)
        {
            Headers = new HeaderDictionary(),
            ContentType = name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                ? "application/pdf" : "text/plain"
        };
    }

    private static ClaimsPrincipal Principal(string account, string role, string department)
    {
        var identity = new ClaimsIdentity([
            new Claim("account", account),
            new Claim(ClaimTypes.NameIdentifier, account),
            new Claim(ClaimTypes.Role, role),
            new Claim("department", department),
            new Claim(SystemAuthorization.DataAccessLevelClaim, SystemAuthorization.DataAccessLevels.Editor),
            new Claim(SystemAuthorization.AccessScopeClaim, SystemAuthorization.AccessScopes.CsitStaff)
        ], "test", ClaimTypes.Name, ClaimTypes.Role);
        return new ClaimsPrincipal(identity);
    }

    private sealed class Environment : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(),
            "csit-document-approval-" + Guid.NewGuid().ToString("N"));
        public InMemoryFactory Factory { get; } = new();
        public Guid CustomFolderId { get; private set; }
        public Guid ProductFolderId { get; private set; }
        public DocumentLibraryApprovalUploadService Upload { get; private set; } = null!;
        public ApprovalSubmissionService Submissions { get; private set; } = null!;
        public ApprovalCommitWorker Commit { get; private set; } = null!;
        public DocumentLibraryArchiveWorker Archive { get; private set; } = null!;
        public DocumentLibraryService Formal { get; private set; } = null!;

        public static async Task<Environment> CreateAsync()
        {
            var env = new Environment();
            Directory.CreateDirectory(env.ApprovalRoot);
            Directory.CreateDirectory(env.DocumentStagingRoot);
            Directory.CreateDirectory(env.LibraryRoot);
            await env.SeedAsync();
            env.BuildServices();
            return env;
        }

        private string ApprovalRoot => Path.Combine(root, "approval");
        private string DocumentStagingRoot => Path.Combine(root, "document-staging");
        private string LibraryRoot => Path.Combine(root, "library");

        private async Task SeedAsync()
        {
            var now = DateTime.UtcNow;
            var customL1 = Folder(1, "Custom Root", null, now);
            var customL2 = Folder(2, "Custom Group", customL1.Id, now);
            var customL3 = Folder(3, "Custom Files", customL2.Id, now);
            var productL1 = Folder(1, "產品驗證資料", null, now);
            var productL2 = Folder(2, "Model A", productL1.Id, now);
            var productL3 = Folder(3, "產品資訊", productL2.Id, now);
            CustomFolderId = customL3.Id;
            ProductFolderId = productL3.Id;
            var teamId = Guid.NewGuid();
            await using var db = await Factory.CreateDbContextAsync();
            db.DocumentFolders.AddRange(customL1, customL2, customL3, productL1, productL2, productL3);
            db.MenuItems.Add(new MenuItem
            {
                Id = Guid.NewGuid(), SectionId = Guid.NewGuid(),
                Code = ApprovalSourceMenuBinding.DocumentLibraryMenu, Title = "文件專區",
                RoutePath = "/documents", IsEnabled = true, RequiresSupervisorApproval = false,
                CreatedAt = now, UpdatedAt = now
            });
            db.SystemOptions.Add(new SystemOption
            {
                Id = teamId, Category = SystemOptionCategories.Team, Name = "Team A", Value = "TEAM-A",
                IsEnabled = true, CreatedAt = now, UpdatedAt = now
            });
            db.TeamRoutings.Add(new TeamRouting
            {
                Id = Guid.NewGuid(), TeamOptionId = teamId, LeaderAccount = "leader",
                LeaderDisplayName = "Leader", IsEnabled = true, CreatedAt = now, UpdatedAt = now
            });
            db.Users.AddRange(
                User("admin", "Administration"), User("leader", "Team A"),
                User("employee", "Team A"), User("unrouteable", "Unknown Department"));
            await db.SaveChangesAsync();
        }

        private void BuildServices()
        {
            var roots = new FixedRoot(ApprovalRoot);
            var staging = new ApprovalStagingStorage(roots);
            Formal = new DocumentLibraryService(Factory,
                Options.Create(new DocumentStorageOptions
                    { RootPath = LibraryRoot, MaxFileSizeBytes = 200_000_000 }),
                Options.Create(new DocumentStagingOptions { RootPath = DocumentStagingRoot }),
                new AllowWrite(), Options.Create(new KmIntegrationOptions { Enabled = false }));
            var handler = new DocumentLibraryUploadApprovalCommitHandler(Factory, staging, Formal);
            var registry = new ApprovalCommitHandlerRegistry([handler]);
            var auth = new TestAuthorization(Factory);
            var reviewer = new ApprovalReviewerScopeResolver(Factory, auth);
            Submissions = new ApprovalSubmissionService(Factory, reviewer, registry, roots, staging,
                NullLogger<ApprovalSubmissionService>.Instance);
            Upload = new DocumentLibraryApprovalUploadService(Factory, new ApprovalMenuResolver(Factory),
                registry, staging, Submissions, Formal, auth,
                Options.Create(new DocumentStorageOptions
                    { RootPath = LibraryRoot, MaxFileSizeBytes = 200_000_000 }));
            Commit = new ApprovalCommitWorker(Factory, registry,
                NullLogger<ApprovalCommitWorker>.Instance);
            Archive = new DocumentLibraryArchiveWorker(Factory,
                Options.Create(new KmIntegrationOptions
                {
                    Enabled = false, ArchivePollSeconds = 1, ArchiveLeaseSeconds = 8,
                    ArchiveMaxRetryCount = 3, ArchiveRetryScheduleSeconds = [1]
                }),
                Options.Create(new DocumentStorageOptions { RootPath = LibraryRoot }),
                Options.Create(new DocumentStagingOptions { RootPath = DocumentStagingRoot }),
                NullLogger<DocumentLibraryArchiveWorker>.Instance);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            return ValueTask.CompletedTask;
        }

        private static DocumentFolder Folder(int level, string name, Guid? parentId, DateTime now) => new()
        {
            Id = Guid.NewGuid(), ParentId = parentId, Level = level, Name = name,
            CreatedAt = now, UpdatedAt = now
        };

        private static AppUser User(string account, string department) => new()
        {
            Id = Guid.NewGuid(), Account = account, DisplayName = account,
            Department = department, Email = $"{account}@example.test"
        };
    }

    private sealed class FixedRoot(string root) : IApprovalStagingRootResolver
    {
        public Task<string> GetRootAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(root);
    }

    private sealed class TestAuthorization(IDbContextFactory<AppDbContext> factory) : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource,
            IEnumerable<IAuthorizationRequirement> requirements) =>
            Task.FromResult(AuthorizationResult.Success());

        public async Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource,
            string policyName)
        {
            if (policyName == SystemAuthorization.Policies.BusinessWrite)
                return user.HasClaim(SystemAuthorization.DataAccessLevelClaim,
                    SystemAuthorization.DataAccessLevels.Editor)
                    ? AuthorizationResult.Success() : AuthorizationResult.Failed();
            if (policyName != SystemAuthorization.Policies.Administration)
                return AuthorizationResult.Success();
            if (user.IsInRole("Admin")) return AuthorizationResult.Success();
            var account = user.FindFirstValue("account")?.ToLowerInvariant();
            await using var db = await factory.CreateDbContextAsync();
            var leader = await (from routing in db.TeamRoutings
                join team in db.SystemOptions on routing.TeamOptionId equals team.Id
                where routing.IsEnabled && team.IsEnabled && team.Category == SystemOptionCategories.Team &&
                      routing.LeaderAccount.ToLower() == account
                select routing.Id).AnyAsync();
            return leader ? AuthorizationResult.Success() : AuthorizationResult.Failed();
        }
    }

    private sealed class AllowWrite : IBusinessWriteAuthorizationGuard
    {
        public Task DemandAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class InMemoryFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> options =
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        public AppDbContext CreateDbContext() => new(options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
