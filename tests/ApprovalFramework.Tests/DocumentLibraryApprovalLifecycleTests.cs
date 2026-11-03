using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models;
using SIT.DepartmentSystem.Web.Models.Api;
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
    public async Task SameDepartmentDifferentTeamCodes_RouteToTheirOwnLeadersAndRemainIsolated()
    {
        await using var env = await Environment.CreateAsync();
        var first = await env.Upload.UploadAsync(env.CustomFolderId,
            File("team-a.txt", "team-a"), Principal("employee", "User", "Claim Department"));
        var second = await env.Upload.UploadAsync(env.CustomFolderId,
            File("team-b.txt", "team-b"), Principal("employee-b", "User", "Claim Department"));

        await using (var db = await env.Factory.CreateDbContextAsync())
        {
            var rows = await db.PendingSubmissions.OrderBy(x => x.SubmittedByAccount).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Equal(env.TeamAId, rows[0].SubmittedTeamOptionId);
            Assert.Equal("TEAM-A", rows[0].SubmittedTeamCode);
            Assert.Equal("Shared Department", rows[0].SubmittedDepartment);
            Assert.Equal(env.TeamBId, rows[1].SubmittedTeamOptionId);
            Assert.Equal("TEAM-B", rows[1].SubmittedTeamCode);
            Assert.Equal("Shared Department", rows[1].SubmittedDepartment);
        }

        var leaderA = Principal("leader", "User", "Any Department");
        var leaderB = Principal("otherleader", "User", "Any Department");
        var leaderAItems = await env.Submissions.ListForReviewAsync(
            leaderA, ApprovalSourceMenuBinding.DocumentLibraryMenu);
        var leaderBItems = await env.Submissions.ListForReviewAsync(
            leaderB, ApprovalSourceMenuBinding.DocumentLibraryMenu);
        Assert.Contains(leaderAItems, x => x.Id == first.SubmissionId);
        Assert.DoesNotContain(leaderAItems, x => x.Id == second.SubmissionId);
        Assert.Contains(leaderBItems, x => x.Id == second.SubmissionId);
        Assert.DoesNotContain(leaderBItems, x => x.Id == first.SubmissionId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            env.Submissions.ApproveAsync(second.SubmissionId!.Value, leaderA));
    }

    [Fact]
    public async Task UntrustedTeamCodeClaim_CannotOverrideSharedIdentityTeam()
    {
        await using var env = await Environment.CreateAsync();
        var pending = await env.Upload.UploadAsync(env.CustomFolderId,
            File("claim-spoof.txt", "content"),
            Principal("employee", "User", "Wrong Department", "TEAM-B"));

        await using var db = await env.Factory.CreateDbContextAsync();
        var row = await db.PendingSubmissions.SingleAsync(x => x.Id == pending.SubmissionId);
        Assert.Equal(env.TeamAId, row.SubmittedTeamOptionId);
        Assert.Equal("TEAM-A", row.SubmittedTeamCode);
    }

    [Theory]
    [InlineData("missing-team-code")]
    [InlineData("disabled-account")]
    [InlineData("disabled-system-access")]
    [InlineData("invalid-role")]
    [InlineData("read-only-account")]
    [InlineData("unknown-team")]
    [InlineData("disabled-team")]
    [InlineData("missing-routing")]
    [InlineData("disabled-routing")]
    [InlineData("missing-leader")]
    [InlineData("conflicting-routing")]
    public async Task InvalidIdentityOrRoutingConfiguration_FailsClosedWithoutStaging(string scenario)
    {
        await using var env = await Environment.CreateAsync();
        await env.ConfigureFailureAsync(scenario);

        await Assert.ThrowsAsync<InvalidOperationException>(() => env.Upload.UploadAsync(
            env.CustomFolderId, File("blocked.txt", "blocked"),
            Principal("employee", "User", "Team A")));

        await using var db = await env.Factory.CreateDbContextAsync();
        Assert.Empty(db.PendingSubmissions);
        Assert.Empty(db.DocumentLibraryFiles);
        Assert.Empty(db.KmIntegrationOutbox);
        Assert.Empty(Directory.GetFiles(env.ApprovalRootPath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SharedIdentityUnavailableOrSubjectAccountMismatch_NeverFallsBackToDepartment()
    {
        await using var env = await Environment.CreateAsync();
        env.FailIdentityAccountList = true;
        var unavailable = await Assert.ThrowsAsync<InvalidOperationException>(() => env.Upload.UploadAsync(
            env.CustomFolderId, File("unavailable.txt", "blocked"),
            Principal("employee", "User", "Team A")));
        Assert.Contains("無法讀取 Shared Identity", unavailable.Message);

        env.FailIdentityAccountList = false;
        var mismatch = await Assert.ThrowsAsync<InvalidOperationException>(() => env.Upload.UploadAsync(
            env.CustomFolderId, File("mismatch.txt", "blocked"),
            Principal("employee", "User", "Team A", subject: IdentityId("employee-b"))));
        Assert.Contains("帳號無效", mismatch.Message);

        await using var db = await env.Factory.CreateDbContextAsync();
        Assert.Empty(db.PendingSubmissions);
        Assert.Empty(db.DocumentLibraryFiles);
    }

    [Fact]
    public async Task MissingRoutingAndLegacyFilesWithoutEvidence_FailClosed()
    {
        await using var env = await Environment.CreateAsync();
        var unrouteable = Principal("unrouteable", "User", "Unknown Department");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            env.Upload.UploadAsync(env.CustomFolderId, File("blocked.txt", "blocked"), unrouteable));
        Assert.Contains("缺少 TeamCode", error.Message);

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
        var remaining = new List<DocumentLibraryFileDto>();
        for (var index = 4; index <= 10; index++)
            remaining.Add((await env.Upload.UploadAsync(
                index % 2 == 0 ? env.ProductFolderId : env.CustomFolderId,
                File($"batch-{index}.txt", $"content-{index}"), admin)).File!);

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

        var tenFileIds = new[] { first.Id, second.Id, third.Id }
            .Concat(remaining.Select(x => x.Id)).ToArray();
        var rows = await env.Formal.GetArchiveStatusesAsync(
            [.. tenFileIds, second.Id, Guid.NewGuid()]);

        Assert.Equal(10, rows.Count);
        Assert.Equal(10, rows.Select(x => x.FileId).Distinct().Count());
        Assert.Equal(7, rows.Count(x => x.ArchiveStatus == DocumentArchiveStatuses.Pending));
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

    private static ClaimsPrincipal Principal(string account, string role, string department,
        string? untrustedTeamCode = null, Guid? subject = null)
    {
        var claims = new List<Claim>
        {
            new Claim("account", account),
            new Claim(ClaimTypes.NameIdentifier, account),
            new Claim(ClaimTypes.Role, role),
            new Claim("department", department),
            new Claim("identity_sub", (subject ?? IdentityId(account)).ToString()),
            new Claim(SystemAuthorization.DataAccessLevelClaim, SystemAuthorization.DataAccessLevels.Editor),
            new Claim(SystemAuthorization.AccessScopeClaim, SystemAuthorization.AccessScopes.CsitStaff)
        };
        if (untrustedTeamCode is not null) claims.Add(new Claim("team_code", untrustedTeamCode));
        var identity = new ClaimsIdentity(claims, "test", ClaimTypes.Name, ClaimTypes.Role);
        return new ClaimsPrincipal(identity);
    }

    private static Guid IdentityId(string account) => account switch
    {
        "admin" => Guid.Parse("10000000-0000-0000-0000-000000000001"),
        "leader" => Guid.Parse("10000000-0000-0000-0000-000000000002"),
        "otherleader" => Guid.Parse("10000000-0000-0000-0000-000000000003"),
        "employee" => Guid.Parse("10000000-0000-0000-0000-000000000004"),
        "employee-b" => Guid.Parse("10000000-0000-0000-0000-000000000005"),
        "unrouteable" => Guid.Parse("10000000-0000-0000-0000-000000000006"),
        _ => Guid.Parse("10000000-0000-0000-0000-000000000099")
    };

    private sealed class Environment : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(),
            "csit-document-approval-" + Guid.NewGuid().ToString("N"));
        public InMemoryFactory Factory { get; } = new();
        public Guid CustomFolderId { get; private set; }
        public Guid ProductFolderId { get; private set; }
        public Guid TeamAId { get; private set; }
        public Guid TeamBId { get; private set; }
        private TestIdentityDirectory Identity { get; } = new();
        public bool FailIdentityAccountList
        {
            set => Identity.FailAccountList = value;
        }
        public string ApprovalRootPath => ApprovalRoot;
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
            TeamAId = Guid.NewGuid();
            TeamBId = Guid.NewGuid();
            await using var db = await Factory.CreateDbContextAsync();
            db.DocumentFolders.AddRange(customL1, customL2, customL3, productL1, productL2, productL3);
            db.MenuItems.Add(new MenuItem
            {
                Id = Guid.NewGuid(), SectionId = Guid.NewGuid(),
                Code = ApprovalSourceMenuBinding.DocumentLibraryMenu, Title = "文件專區",
                RoutePath = "/documents", IsEnabled = true, RequiresSupervisorApproval = false,
                CreatedAt = now, UpdatedAt = now
            });
            db.SystemOptions.AddRange(
                new SystemOption
                {
                    Id = TeamAId, Category = SystemOptionCategories.Team, Name = "Team A", Value = "TEAM-A",
                    IsEnabled = true, CreatedAt = now, UpdatedAt = now
                },
                new SystemOption
                {
                    Id = TeamBId, Category = SystemOptionCategories.Team, Name = "Team B", Value = "TEAM-B",
                    IsEnabled = true, CreatedAt = now, UpdatedAt = now
                });
            db.TeamRoutings.AddRange(
                new TeamRouting
                {
                    Id = Guid.NewGuid(), TeamOptionId = TeamAId, LeaderAccount = "leader",
                    LeaderDisplayName = "Leader", IsEnabled = true, CreatedAt = now, UpdatedAt = now
                },
                new TeamRouting
                {
                    Id = Guid.NewGuid(), TeamOptionId = TeamBId, LeaderAccount = "otherleader",
                    LeaderDisplayName = "Other Leader", IsEnabled = true, CreatedAt = now, UpdatedAt = now
                });
            db.Users.AddRange(
                User("admin", "Administration"), User("leader", "Team A"),
                User("otherleader", "Team B"), User("employee", "Legacy Department"),
                User("employee-b", "Legacy Department"), User("unrouteable", "Unknown Department"));
            await db.SaveChangesAsync();

            Identity.Accounts.AddRange(
                IdentityAccount("leader", "TEAM-A"),
                IdentityAccount("otherleader", "TEAM-B"),
                IdentityAccount("employee", "TEAM-A"),
                IdentityAccount("employee-b", "TEAM-B"),
                IdentityAccount("unrouteable", null));
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
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["SharedIdentity:ProvisioningClientId"] = "test-client",
                    ["SharedIdentity:ProvisioningClientSecret"] = "test-secret"
                }).Build();
            var identityAccounts = new SharedIdentityProvisioningClient(
                new TestIdentityHttpClientFactory(Identity), configuration,
                NullLogger<SharedIdentityProvisioningClient>.Instance);
            Submissions = new ApprovalSubmissionService(Factory, reviewer, registry, roots, staging,
                NullLogger<ApprovalSubmissionService>.Instance);
            Upload = new DocumentLibraryApprovalUploadService(Factory, new ApprovalMenuResolver(Factory),
                registry, staging, Submissions, Formal, identityAccounts, auth,
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

        public async Task ConfigureFailureAsync(string scenario)
        {
            if (scenario == "missing-team-code")
            {
                Identity.Replace("employee", account => account with { TeamCode = null });
                return;
            }
            if (scenario == "unknown-team")
            {
                Identity.Replace("employee", account => account with { TeamCode = "TEAM-MISSING" });
                return;
            }
            if (scenario == "disabled-account")
            {
                Identity.Replace("employee", account => account with { AccountStatus = "Disabled" });
                return;
            }
            if (scenario == "disabled-system-access")
            {
                Identity.Replace("employee", account => account with { SystemAccessStatus = "Disabled" });
                return;
            }
            if (scenario == "invalid-role")
            {
                Identity.Replace("employee", account => account with { CsitRole = "RdApplicant" });
                return;
            }
            if (scenario == "read-only-account")
            {
                Identity.Replace("employee", account => account with { DataAccessLevel = "ReadOnly" });
                return;
            }

            await using var db = await Factory.CreateDbContextAsync();
            var team = await db.SystemOptions.SingleAsync(x => x.Id == TeamAId);
            var routing = await db.TeamRoutings.SingleAsync(x => x.TeamOptionId == TeamAId);
            switch (scenario)
            {
                case "disabled-team":
                    team.IsEnabled = false;
                    break;
                case "missing-routing":
                    db.TeamRoutings.Remove(routing);
                    break;
                case "disabled-routing":
                    routing.IsEnabled = false;
                    break;
                case "missing-leader":
                    routing.LeaderAccount = string.Empty;
                    break;
                case "conflicting-routing":
                    db.TeamRoutings.Add(new TeamRouting
                    {
                        Id = Guid.NewGuid(), TeamOptionId = TeamAId, LeaderAccount = "otherleader",
                        LeaderDisplayName = "Conflicting Leader", IsEnabled = true,
                        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
                    });
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario));
            }
            await db.SaveChangesAsync();
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
            Id = Guid.NewGuid(), IdentityUserId = IdentityId(account), Account = account, DisplayName = account,
            Department = department, Email = $"{account}@example.test"
        };

        private static SharedIdentityAccountListItem IdentityAccount(string account, string? teamCode) => new(
            IdentityId(account), account.ToUpperInvariant(), account, account,
            "Shared Department", teamCode, "Engineer", null, $"{account}@askey.com",
            "CsitStaff", "Editor", "Active", "Active", DateTimeOffset.UtcNow,
            null, null);
    }

    private sealed class TestIdentityDirectory
    {
        public List<SharedIdentityAccountListItem> Accounts { get; } = [];
        public bool FailAccountList { get; set; }

        public void Replace(string account,
            Func<SharedIdentityAccountListItem, SharedIdentityAccountListItem> replace)
        {
            var index = Accounts.FindIndex(x => string.Equals(x.Account, account,
                StringComparison.OrdinalIgnoreCase));
            if (index < 0) throw new InvalidOperationException($"Missing test account {account}.");
            Accounts[index] = replace(Accounts[index]);
        }
    }

    private sealed class TestIdentityHttpClientFactory(TestIdentityDirectory directory) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new TestIdentityHttpHandler(directory))
        {
            BaseAddress = new Uri("http://identity.test/")
        };
    }

    private sealed class TestIdentityHttpHandler(TestIdentityDirectory directory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/connect/token", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { access_token = "test-token" })
                });
            if (directory.FailAccountList)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(directory.Accounts)
            });
        }
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
