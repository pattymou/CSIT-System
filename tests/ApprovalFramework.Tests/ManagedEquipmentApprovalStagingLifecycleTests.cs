using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Config;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Approval;
using SIT.DepartmentSystem.Web.Services.Implementations;

namespace ApprovalFramework.Tests;

public sealed class ManagedEquipmentApprovalStagingLifecycleTests
{
    [Fact]
    public async Task PendingApprovalArchivesManagedPhotoCommitsAndThenCleansStaging()
    {
        using var env = new Environment();

        Assert.Equal(ApprovalUploadDecisionKind.Pending,
            await env.Upload.UploadAsync("equipment", env.EquipmentId,
                [File("front.jpg", "image/jpeg")], User("engineer")));
        var pending = await env.LatestAsync();
        var stagingPath = env.StagingPath(pending);
        Assert.Equal(ApprovalStatus.Pending, pending.Status);
        Assert.True(System.IO.File.Exists(stagingPath));

        await env.Submissions.ApproveAsync(pending.Id, User("leader", supervisor: true));
        Assert.Equal(ApprovalStatus.ApprovedPendingCommit, await env.StatusAsync(pending.Id));
        await env.Worker.ProcessAsync(pending.Id);

        Assert.Equal(ApprovalStatus.Committed, await env.StatusAsync(pending.Id));
        await using (var db = env.Factory.CreateDbContext())
        {
            var formal = await db.ApparatusFiles.SingleAsync(x => x.Id == pending.Id);
            Assert.Equal(FileArchiveStatuses.Archived, formal.ArchiveStatus);
            Assert.Equal(pending.FileHash, formal.Sha256, ignoreCase: true);
            Assert.False(formal.IsRawDataExported);
            Assert.Null(formal.NasFolderPath);
            Assert.True(System.IO.File.Exists(Path.Combine(formal.FilePath, formal.FileName)));
            Assert.False(await db.KmIntegrationOutbox.AnyAsync(x =>
                x.SourceEntityId == formal.Id));
            Assert.True(await db.ApprovalAuditEvents.AnyAsync(x => x.SubmissionId == pending.Id &&
                x.Action == ApprovalAuditActions.Committed));
        }

        Assert.Equal(1, await env.Submissions.CleanInactiveStagingBatchAsync());
        Assert.False(System.IO.File.Exists(stagingPath));
        await using var verify = env.Factory.CreateDbContext();
        Assert.Null((await verify.PendingSubmissions.SingleAsync(x => x.Id == pending.Id))
            .StagingStorageKey);
        Assert.Equal(4, await verify.ApprovalAuditEvents.CountAsync(x => x.SubmissionId == pending.Id));
        Assert.Single(await verify.ApparatusFiles.Where(x => x.Id == pending.Id).ToListAsync());
    }

    [Fact]
    public async Task FormalArchiveFailureLeavesCommitFailedStagingForRetry()
    {
        using var env = new Environment();
        Assert.Equal(ApprovalUploadDecisionKind.Pending,
            await env.Upload.UploadAsync("equipment", env.EquipmentId,
                [File("blocked.exe", "application/octet-stream")], User("engineer")));
        var pending = await env.LatestAsync();
        var stagingPath = env.StagingPath(pending);

        await env.Submissions.ApproveAsync(pending.Id, User("leader", supervisor: true));
        await env.Worker.ProcessAsync(pending.Id);

        Assert.Equal(ApprovalStatus.CommitFailed, await env.StatusAsync(pending.Id));
        Assert.True(System.IO.File.Exists(stagingPath));
        Assert.Equal(0, await env.Submissions.CleanInactiveStagingBatchAsync());
        await using var db = env.Factory.CreateDbContext();
        Assert.NotNull((await db.PendingSubmissions.SingleAsync(x => x.Id == pending.Id))
            .StagingStorageKey);
        Assert.False(await db.ApparatusFiles.AnyAsync(x => x.Id == pending.Id));
    }

    private static IFormFile File(string name, string contentType)
    {
        var bytes = "approved-equipment-photo"u8.ToArray();
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "files", name)
        {
            Headers = new HeaderDictionary(), ContentType = contentType
        };
    }

    private static ClaimsPrincipal User(string account, bool supervisor = false)
    {
        var claims = new List<Claim> { new("account", account), new("write", "true") };
        if (supervisor) claims.Add(new("supervisor", "true"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private sealed class Environment : IDisposable
    {
        public Factory Factory { get; } = new();
        public string Root { get; } = Path.Combine(Path.GetTempPath(),
            "csit-managed-equipment-approval-" + Guid.NewGuid().ToString("N"));
        public string EquipmentId { get; } = "EQ-" + Guid.NewGuid().ToString("N")[..12];
        public ApprovalSubmissionService Submissions { get; }
        public AssetApprovalUploadService Upload { get; }
        public ApprovalCommitWorker Worker { get; }
        private readonly RootResolver stagingRoot;
        private readonly AppDbContext formalDb;

        public Environment()
        {
            var stagingPath = Path.Combine(Root, "staging");
            var formalRoot = Path.Combine(Root, "formal");
            Directory.CreateDirectory(stagingPath);
            Directory.CreateDirectory(formalRoot);
            stagingRoot = new RootResolver(stagingPath);
            var teamId = Guid.NewGuid();
            var departmentId = Guid.NewGuid();
            using (var db = Factory.CreateDbContext())
            {
                db.MenuItems.Add(new MenuItem
                {
                    Id = Guid.NewGuid(), Code = "equipment", Title = "Equipment",
                    ModuleCode = "equipment", IsEnabled = true, RequiresSupervisorApproval = true
                });
                db.SystemOptions.AddRange(
                    new SystemOption
                    {
                        Id = teamId, Category = SystemOptionCategories.Team,
                        Name = "TEAM-A", Value = "TEAM-A", IsEnabled = true
                    },
                    new SystemOption
                    {
                        Id = departmentId, Category = SystemOptionCategories.Department,
                        Name = "R&D", Value = "R&D", IsEnabled = true
                    },
                    new SystemOption
                    {
                        Id = Guid.NewGuid(), Category = "AssetRootPath", Name = "equipment",
                        Value = formalRoot, IsEnabled = true
                    });
                db.TeamRoutings.Add(new TeamRouting
                {
                    Id = Guid.NewGuid(), TeamOptionId = teamId, LeaderAccount = "leader",
                    LeaderDisplayName = "Leader", IsEnabled = true
                });
                db.Apparatuses.Add(new Apparatus
                {
                    Id = EquipmentId, ModuleCode = "equipment", Name = "Managed Equipment",
                    ProductsId = "PROP-001", Kind = "Tester", OwnerTeamOptionId = teamId,
                    DepartmentOptionId = departmentId, CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
                db.SaveChanges();
            }

            var staging = new ApprovalStagingStorage(stagingRoot);
            var source = new ApprovalSourceResolver(Factory);
            formalDb = Factory.CreateDbContext();
            var rawOptions = Options.Create(new RawDataSettings { Enabled = false });
            var formal = new ApparatusService(formalDb, Options.Create(new ApparatusSettings()),
                rawOptions, new RawDataExportService(rawOptions), null!,
                new ManagedAssetFileStorage(formalDb));
            var handler = new AssetUploadApprovalCommitHandler("equipment", source, staging,
                formal, Factory);
            var handlers = new ApprovalCommitHandlerRegistry([handler]);
            var authorization = new Authorization();
            Submissions = new ApprovalSubmissionService(Factory,
                new ApprovalReviewerScopeResolver(Factory, authorization), handlers,
                stagingRoot, staging, NullLogger<ApprovalSubmissionService>.Instance,
                Options.Create(new ApprovalStagingCleanupOptions
                {
                    EnableNewSubmissionCleanup = true,
                    NewSubmissionNotBeforeUtc = DateTime.UtcNow.AddMinutes(-1),
                    EnableHistoricalCleanup = false
                }));
            Upload = new AssetApprovalUploadService(source,
                new ApprovalPolicyResolver(new ApprovalMenuResolver(Factory), handlers,
                    stagingRoot, authorization), Submissions, staging, formal);
            Worker = new ApprovalCommitWorker(Factory, handlers,
                NullLogger<ApprovalCommitWorker>.Instance);
        }

        public async Task<PendingSubmission> LatestAsync()
        {
            await using var db = Factory.CreateDbContext();
            return await db.PendingSubmissions.AsNoTracking()
                .OrderByDescending(x => x.SubmittedAt).FirstAsync();
        }

        public async Task<ApprovalStatus> StatusAsync(Guid id)
        {
            await using var db = Factory.CreateDbContext();
            return await db.PendingSubmissions.Where(x => x.Id == id)
                .Select(x => x.Status).SingleAsync();
        }

        public string StagingPath(PendingSubmission item) => Path.Combine(stagingRoot.Root,
            item.StagingStorageKey!.Replace('/', Path.DirectorySeparatorChar));

        public void Dispose()
        {
            formalDb.Dispose();
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

    private sealed class Authorization : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource,
            IEnumerable<IAuthorizationRequirement> requirements) =>
            Task.FromResult(AuthorizationResult.Failed());

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource,
            string policyName) => Task.FromResult(policyName switch
            {
                SystemAuthorization.Policies.BusinessWrite when user.HasClaim("write", "true") =>
                    AuthorizationResult.Success(),
                SystemAuthorization.Policies.Administration when user.HasClaim("supervisor", "true") =>
                    AuthorizationResult.Success(),
                _ => AuthorizationResult.Failed()
            });
    }
}
