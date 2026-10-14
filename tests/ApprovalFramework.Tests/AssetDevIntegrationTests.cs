using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Config;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Approval;
using SIT.DepartmentSystem.Web.Services.Implementations;

namespace ApprovalFramework.Tests;

[CollectionDefinition("Dev approval integration", DisableParallelization = true)]
public sealed class DevApprovalIntegrationCollection { }

[Collection("Dev approval integration")]
public sealed class AssetDevIntegrationTests
{
    [Theory]
    [InlineData("equipment")]
    [InlineData("goods")]
    [Trait("Category", "DevIntegration")]
    public async Task DevAssetDirectPendingCommitRejectBypassAndRetry(string moduleCode)
    {
        var connection = Environment.GetEnvironmentVariable("CSIT_DEV_APPROVAL_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) return;
        var connectionInfo = new NpgsqlConnectionStringBuilder(connection);
        Assert.Equal("sit_department_system_dev", connectionInfo.Database);
        Assert.True(connectionInfo.Host is "localhost" or "127.0.0.1");
        Assert.Equal("csit_runtime_dev", connectionInfo.Username);

        const string storageBase = @"D:\CSIT_Dev_Data";
        var stagingRoot = Path.Combine(storageBase, "ApprovalStaging");
        var formalRoot = Path.Combine(storageBase, "Assets", moduleCode);
        var rawRoot = Path.Combine(storageBase, "RawData");
        Assert.True(Directory.Exists(stagingRoot) && Directory.Exists(formalRoot) && Directory.Exists(rawRoot));

        var factory = new TestDbFactory(connection);
        var tag = (moduleCode == "equipment" ? "DEV-EQ-" : "DEV-GD-")
            + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        string DemoFile(string name)
        {
            var dot = name.LastIndexOf('.');
            return $"{name[..dot]}-{tag[^6..]}{name[dot..]}";
        }
        var directFileName = DemoFile("result-01.txt");
        var bypassFileName = DemoFile("result-05.txt");
        var missingTeamId = tag + "-NT";
        var teamId = Guid.NewGuid();
        var departmentId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.True(await db.MenuItems.AnyAsync(x => x.Code == moduleCode
                && x.ModuleCode == moduleCode && !x.RequiresSupervisorApproval));
            var root = await db.SystemOptions.Where(x => x.Category == "AssetRootPath"
                && x.Name == moduleCode && x.IsEnabled).Select(x => x.Value).SingleAsync();
            Assert.Equal(Path.GetFullPath(formalRoot), Path.GetFullPath(root), ignoreCase: true);
            db.SystemOptions.AddRange(
                new SystemOption { Id = teamId, Category = SystemOptionCategories.Team,
                    Name = tag, Value = tag, IsEnabled = true, CreatedAt = now, UpdatedAt = now },
                new SystemOption { Id = departmentId, Category = SystemOptionCategories.Department,
                    Name = tag, Value = tag, IsEnabled = true, CreatedAt = now, UpdatedAt = now });
            db.TeamRoutings.Add(new TeamRouting { Id = Guid.NewGuid(), TeamOptionId = teamId,
                LeaderAccount = "dev-asset-leader", LeaderDisplayName = "DEV TEST Leader",
                IsEnabled = true, CreatedAt = now, UpdatedAt = now });
            db.Apparatuses.AddRange(
                new Apparatus { Id = tag, ModuleCode = moduleCode, Name = tag,
                    Kind = "DEV TEST", DepartmentOptionId = departmentId,
                    OwnerTeamOptionId = teamId, CreatedAt = now, UpdatedAt = now },
                new Apparatus { Id = missingTeamId, ModuleCode = moduleCode, Name = missingTeamId,
                    Kind = "DEV TEST", DepartmentOptionId = departmentId,
                    CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
        }

        var sourceResolver = new ApprovalSourceResolver(factory);
        var stagingRootResolver = new TestRoot(stagingRoot);
        var staging = new ApprovalStagingStorage(stagingRootResolver);
        var rawSettings = Options.Create(new RawDataSettings { Enabled = true, NasRootPath = rawRoot });
        await using var formalDb = await factory.CreateDbContextAsync();
        var formal = new ApparatusService(formalDb, Options.Create(new ApparatusSettings()),
            rawSettings, new RawDataExportService(rawSettings), null!);
        var equipmentHandler = new AssetUploadApprovalCommitHandler("equipment",
            sourceResolver, staging, formal, factory);
        var goodsHandler = new AssetUploadApprovalCommitHandler("goods",
            sourceResolver, staging, formal, factory);
        var handlers = new ApprovalCommitHandlerRegistry([equipmentHandler, goodsHandler]);
        var handler = handlers.Resolve(moduleCode);
        var auth = new TestAuthorization();
        var policy = new ApprovalPolicyResolver(new ApprovalMenuResolver(factory), handlers,
            stagingRootResolver, auth);
        var scope = new ApprovalReviewerScopeResolver(factory, auth);
        var submissions = new ApprovalSubmissionService(factory, scope, handlers,
            stagingRootResolver, staging);
        var upload = new AssetApprovalUploadService(sourceResolver, policy, submissions, staging, formal);
        var worker = new ApprovalCommitWorker(factory, handlers, NullLogger<ApprovalCommitWorker>.Instance);
        var engineer = User("dev-asset-engineer");
        var leader = User("dev-asset-leader", supervisor: true);
        var otherLeader = User("dev-other-leader", supervisor: true);
        var admin = User("dev-asset-admin", supervisor: true, admin: true);

        // Both Asset menus remain off in DEV; the existing direct upload behavior is preserved.
        Assert.Equal(ApprovalUploadDecisionKind.DirectCommit,
            await upload.UploadAsync(moduleCode, tag, [UploadFile(DemoFile("result-01.txt"))], engineer));
        await using (var db = await factory.CreateDbContextAsync())
        {
            var direct = await db.ApparatusFiles.SingleAsync(x => x.ApparatusId == tag
                && x.FileName == directFileName);
            Assert.True(File.Exists(Path.Combine(direct.FilePath, direct.FileName)));
            Assert.StartsWith(Path.GetFullPath(formalRoot), Path.GetFullPath(direct.FilePath),
                StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain(await submissions.ListActiveSourcesAsync(), x => x.Code == moduleCode);
        Assert.True(handlers.HasHandler("equipment") && handlers.HasHandler("goods"));
        await new ApprovalEnableGuard(handlers, stagingRootResolver, factory, rawSettings)
            .DemandReadyAsync(moduleCode);

        // The temporary DEV flag is restored even when a test assertion fails.
        try
        {
            await SetFlagAsync(factory, moduleCode, true);
            Assert.Contains(await submissions.ListActiveSourcesAsync(), x => x.Code == moduleCode);
            Assert.Equal(ApprovalUploadDecisionKind.Pending,
                await upload.UploadAsync(moduleCode, tag, [UploadFile(DemoFile("result-02.txt"))], engineer));
            var pending = await LatestAsync(factory, moduleCode, tag);
            await using (var stagedFile = await staging.OpenReadAsync(pending.Id, pending.StagingStorageKey!))
                Assert.Equal(pending.FileHash,
                    Convert.ToHexString(await SHA256.HashDataAsync(stagedFile)), ignoreCase: true);
            Assert.Equal(teamId, pending.SubmittedTeamOptionId);
            Assert.Equal(tag, pending.SubmittedDepartment);
            Assert.Equal(tag, pending.SubmittedTeamCode);
            Assert.Contains(tag, pending.ContextDisplay);
            await AssertNoFormalAsync(factory, pending.Id, formalRoot, rawRoot, tag);
            Assert.Contains(await submissions.ListForReviewAsync(leader, moduleCode), x => x.Id == pending.Id);
            Assert.Contains(await submissions.ListForReviewAsync(admin, moduleCode), x => x.Id == pending.Id);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scope.DemandReviewAsync(otherLeader, pending));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scope.DemandReviewAsync(engineer, pending));

            await submissions.ApproveAsync(pending.Id, leader);
            Assert.Equal(ApprovalStatus.ApprovedPendingCommit, await StatusAsync(factory, pending.Id));
            await AssertNoFormalAsync(factory, pending.Id, formalRoot, rawRoot, tag);
            await worker.ProcessAsync(pending.Id);
            Assert.Equal(ApprovalStatus.Committed, await StatusAsync(factory, pending.Id));
            await AssertFormalOnceAsync(factory, pending, formalRoot, rawRoot);
            await worker.ProcessAsync(pending.Id);
            await handler.CommitAsync(await SubmissionAsync(factory, pending.Id), default);
            await AssertFormalOnceAsync(factory, pending, formalRoot, rawRoot);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                handlers.Resolve(moduleCode == "equipment" ? "goods" : "equipment")
                    .ValidateAsync(pending, default));

            Assert.Equal(ApprovalUploadDecisionKind.Pending,
                await upload.UploadAsync(moduleCode, tag, [UploadFile(DemoFile("result-03.txt"))], engineer));
            var rejected = await LatestAsync(factory, moduleCode, tag);
            await Assert.ThrowsAsync<ArgumentException>(() => submissions.RejectAsync(rejected.Id, leader, "   "));
            await submissions.RejectAsync(rejected.Id, leader, "DEV TEST correction required");
            Assert.Equal(ApprovalStatus.Rejected, await StatusAsync(factory, rejected.Id));
            await AssertNoFormalAsync(factory, rejected.Id, formalRoot, rawRoot, tag);
            Assert.Contains(await submissions.ListRejectedMineAsync(engineer), x => x.Id == rejected.Id
                && x.RejectReason == "DEV TEST correction required" && x.SourceTitle != string.Empty);
            Assert.DoesNotContain(await submissions.ListRejectedMineAsync(User("other-engineer")),
                x => x.Id == rejected.Id);

            var revisedId = await upload.ResubmitAsync(moduleCode, tag,
                UploadFile(DemoFile("result-03-revised.txt")), engineer, rejected.Id);
            Assert.NotEqual(rejected.Id, revisedId);
            await using (var db = await factory.CreateDbContextAsync())
            {
                var previous = await db.PendingSubmissions.SingleAsync(x => x.Id == rejected.Id);
                Assert.Equal(ApprovalStatus.Rejected, previous.Status);
                Assert.Equal(revisedId, previous.ResubmittedAsSubmissionId);
                Assert.True(await db.ApprovalAuditEvents.AnyAsync(x => x.SubmissionId == rejected.Id
                    && x.Action == ApprovalAuditActions.Resubmitted));
            }
            Assert.DoesNotContain(await submissions.ListRejectedMineAsync(engineer), x => x.Id == rejected.Id);
            await submissions.ApproveAsync(revisedId, leader);
            await worker.ProcessAsync(revisedId);
            Assert.Equal(ApprovalStatus.Committed, await StatusAsync(factory, revisedId));

            var missingTeam = await Assert.ThrowsAsync<InvalidOperationException>(() => upload.UploadAsync(moduleCode,
                missingTeamId, [UploadFile(DemoFile("result-04.txt"))], engineer));
            Assert.Equal("此項目尚未設定 Team，無法送主管審核，請先完成 Team 設定。", missingTeam.Message);
            await using (var db = await factory.CreateDbContextAsync())
                Assert.False(await db.PendingSubmissions.AnyAsync(x => x.ContextId == missingTeamId));

            var beforeBypass = await CountSubmissionsAsync(factory, moduleCode, tag);
            Assert.Equal(ApprovalUploadDecisionKind.ApprovalBypassed,
                await upload.UploadAsync(moduleCode, tag, [UploadFile(DemoFile("result-05.txt"))], leader));
            Assert.Equal(beforeBypass, await CountSubmissionsAsync(factory, moduleCode, tag));
            await using (var db = await factory.CreateDbContextAsync())
            {
                Assert.True(await db.ApparatusFiles.AnyAsync(x => x.ApparatusId == tag
                    && x.FileName == bypassFileName));
                Assert.True(await db.ApprovalAuditEvents.AnyAsync(x => x.Action == ApprovalAuditActions.ApprovalBypassed
                    && x.SourceModuleCode == moduleCode && x.ActorAccount == "dev-asset-leader"));
            }

            Assert.Equal(ApprovalUploadDecisionKind.Pending,
                await upload.UploadAsync(moduleCode, tag, [UploadFile(DemoFile("result-06.txt"))], engineer));
            var retry = await LatestAsync(factory, moduleCode, tag);
            await submissions.ApproveAsync(retry.Id, leader);
            var stagedPath = Path.Combine(stagingRoot,
                retry.StagingStorageKey!.Replace('/', Path.DirectorySeparatorChar));
            var heldPath = stagedPath + ".hold";
            File.Move(stagedPath, heldPath);
            try
            {
                await worker.ProcessAsync(retry.Id);
                Assert.Equal(ApprovalStatus.CommitFailed, await StatusAsync(factory, retry.Id));
                await AssertNoFormalAsync(factory, retry.Id, formalRoot, rawRoot, tag);
            }
            finally { File.Move(heldPath, stagedPath); }
            await submissions.RetryAsync(retry.Id, leader);
            await worker.ProcessAsync(retry.Id);
            Assert.Equal(ApprovalStatus.Committed, await StatusAsync(factory, retry.Id));
            await AssertFormalOnceAsync(factory, retry, formalRoot, rawRoot);

            // Recover an incomplete physical write that has no formal DB row yet.
            Assert.Equal(ApprovalUploadDecisionKind.Pending,
                await upload.UploadAsync(moduleCode, tag, [UploadFile(DemoFile("result-07.txt"))], engineer));
            var orphan = await LatestAsync(factory, moduleCode, tag);
            await submissions.ApproveAsync(orphan.Id, leader);
            var orphanFolder = Path.Combine(formalRoot, tag, orphan.Id.ToString("N"));
            Directory.CreateDirectory(orphanFolder);
            await File.WriteAllBytesAsync(Path.Combine(orphanFolder, orphan.OriginalFileName!), [0]);
            await worker.ProcessAsync(orphan.Id);
            Assert.Equal(ApprovalStatus.Committed, await StatusAsync(factory, orphan.Id));
            await AssertFormalOnceAsync(factory, orphan, formalRoot, rawRoot);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => upload.UploadAsync(moduleCode,
                tag, [UploadFile(DemoFile("result-08.txt"))], User("read-only", write: false)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => upload.UploadAsync(
                moduleCode == "equipment" ? "goods" : "equipment", tag,
                [UploadFile(DemoFile("result-09.txt"))], engineer));
        }
        finally
        {
            await SetFlagAsync(factory, moduleCode, false);
        }
        Assert.DoesNotContain(await submissions.ListActiveSourcesAsync(), x => x.Code == moduleCode);
        await using (var db = await factory.CreateDbContextAsync())
            Assert.False(await db.MenuItems.Where(x => x.Code == moduleCode)
                .Select(x => x.RequiresSupervisorApproval).SingleAsync());
    }

    private static async Task SetFlagAsync(TestDbFactory factory, string moduleCode, bool enabled)
    {
        await using var db = await factory.CreateDbContextAsync();
        var menu = await db.MenuItems.SingleAsync(x => x.Code == moduleCode && x.ModuleCode == moduleCode);
        menu.RequiresSupervisorApproval = enabled;
        await db.SaveChangesAsync();
    }

    private static async Task<PendingSubmission> LatestAsync(TestDbFactory factory, string moduleCode, string assetId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.PendingSubmissions.AsNoTracking().Where(x => x.SourceModuleCode == moduleCode
            && x.ContextType == "Asset" && x.ContextId == assetId)
            .OrderByDescending(x => x.SubmittedAt).FirstAsync();
    }

    private static async Task<PendingSubmission> SubmissionAsync(TestDbFactory factory, Guid id)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.PendingSubmissions.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    private static async Task<ApprovalStatus> StatusAsync(TestDbFactory factory, Guid id)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.PendingSubmissions.Where(x => x.Id == id).Select(x => x.Status).SingleAsync();
    }

    private static async Task<int> CountSubmissionsAsync(TestDbFactory factory, string moduleCode, string assetId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.PendingSubmissions.CountAsync(x => x.SourceModuleCode == moduleCode
            && x.ContextId == assetId);
    }

    private static async Task AssertNoFormalAsync(TestDbFactory factory, Guid id,
        string formalRoot, string rawRoot, string assetId)
    {
        await using var db = await factory.CreateDbContextAsync();
        Assert.False(await db.ApparatusFiles.AnyAsync(x => x.Id == id));
        Assert.False(Directory.Exists(Path.Combine(formalRoot, assetId, id.ToString("N"))));
        Assert.Empty(Directory.GetFiles(rawRoot, id.ToString("N"), SearchOption.AllDirectories));
    }

    private static async Task AssertFormalOnceAsync(TestDbFactory factory, PendingSubmission pending,
        string formalRoot, string rawRoot)
    {
        await using var db = await factory.CreateDbContextAsync();
        var row = await db.ApparatusFiles.SingleAsync(x => x.Id == pending.Id);
        Assert.Equal(1, await db.ApparatusFiles.CountAsync(x => x.Id == pending.Id));
        Assert.StartsWith(Path.GetFullPath(formalRoot), Path.GetFullPath(row.FilePath),
            StringComparison.OrdinalIgnoreCase);
        Assert.True(row.IsRawDataExported);
        Assert.NotNull(row.NasFolderPath);
        Assert.StartsWith(Path.GetFullPath(rawRoot), Path.GetFullPath(row.NasFolderPath),
            StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(row.RawJsonPath));
        var formal = Path.Combine(row.FilePath, row.FileName);
        var mirrored = Path.Combine(row.NasFolderPath!, row.Id.ToString("N"), row.FileName);
        Assert.True(File.Exists(formal));
        Assert.True(File.Exists(mirrored));
        await using var local = File.OpenRead(formal);
        await using var raw = File.OpenRead(mirrored);
        Assert.Equal(pending.FileHash, Convert.ToHexString(await SHA256.HashDataAsync(local)), ignoreCase: true);
        Assert.Equal(pending.FileHash, Convert.ToHexString(await SHA256.HashDataAsync(raw)), ignoreCase: true);
    }

    private static IFormFile UploadFile(string name)
    {
        byte[] bytes = [1, 2, 3, 4, 5, 6];
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "files", name)
        { Headers = new HeaderDictionary(), ContentType = "application/octet-stream" };
    }

    private static ClaimsPrincipal User(string account, bool write = true,
        bool supervisor = false, bool admin = false)
    {
        var claims = new List<Claim> { new("account", account) };
        if (write) claims.Add(new("can_write", "true"));
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
