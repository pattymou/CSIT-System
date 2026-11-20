using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Controllers;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace ApprovalFramework.Tests;

public sealed class KmQueueMonitorTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    [Fact]
    public void QueueDtoDoesNotExposeStoragePathsAndControllerRequiresDedicatedPolicy()
    {
        Assert.DoesNotContain(typeof(KmQueueItemDto).GetProperties(), property =>
            property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase));
        var authorize = typeof(KmQueueController).GetCustomAttribute<AuthorizeAttribute>();
        Assert.Equal(SystemAuthorization.Policies.KmQueueRead, authorize?.Policy);
    }

    [Fact]
    public void QueuePageUsesScopedDarkSurfacesAndVisibleInteractionStates()
    {
        var css = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Components", "Pages",
            "System", "KmQueue.razor.css"));
        Assert.Contains(".kmq-table-wrap", css);
        Assert.Contains("background: rgba(5, 24, 36, .88);", css);
        Assert.Contains(".kmq-table tbody tr:hover td", css);
        Assert.Contains(".kmq-tabs button.is-active", css);
        Assert.Contains(".kmq-stats button.is-active", css);
        Assert.Contains(".kmq-status.is-waiting", css);
        Assert.Contains(".kmq-status.is-processing", css);
        Assert.Contains(".kmq-status.is-completed", css);
        Assert.Contains(".kmq-status.is-failed", css);
        Assert.DoesNotContain("background: #fff", css, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("background:#fff", css, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DedicatedPolicyAllowsAdminAndEnabledTeamLeaderOnly()
    {
        var factory = new InMemoryFactory();
        var teamId = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.SystemOptions.Add(new SystemOption
            {
                Id = teamId, Category = SystemOptionCategories.Team, Name = "Team A",
                Value = "TEAM-A", IsEnabled = true, CreatedAt = Now, UpdatedAt = Now
            });
            db.TeamRoutings.Add(new TeamRouting
            {
                Id = Guid.NewGuid(), TeamOptionId = teamId, LeaderAccount = "leader",
                LeaderDisplayName = "Leader", IsEnabled = true, CreatedAt = Now, UpdatedAt = Now
            });
            await db.SaveChangesAsync();
        }

        var handler = new KmQueueReadHandler(factory);
        Assert.True(await IsAuthorizedAsync(handler, User("admin", "Admin", true)));
        Assert.True(await IsAuthorizedAsync(handler, User("LEADER", null, true)));
        Assert.False(await IsAuthorizedAsync(handler, User("staff", null, true)));
        Assert.False(await IsAuthorizedAsync(handler, User("leader", null, false)));

        await using (var db = await factory.CreateDbContextAsync())
        {
            (await db.SystemOptions.SingleAsync()).IsEnabled = false;
            await db.SaveChangesAsync();
        }
        Assert.False(await IsAuthorizedAsync(handler, User("leader", null, true)));
    }

    [Fact]
    public async Task IngestionDefaultHidesOnlyCompletedAndCallbackFailureIsNotAutoRetry()
    {
        var factory = new InMemoryFactory();
        var waitingRetry = Outbox(KmIntegrationStatus.Failed, Now.AddMinutes(-3),
            nextRetryAt: Now.AddMinutes(5));
        var callbackFailure = Outbox(KmIntegrationStatus.Failed, Now.AddMinutes(-2),
            kmJobId: "km-job-1", sentAt: Now.AddMinutes(-1));
        var completed = Outbox(KmIntegrationStatus.Completed, Now.AddMinutes(-4),
            completedAt: Now);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.KmIntegrationOutbox.AddRange(waitingRetry, callbackFailure, completed);
            await db.SaveChangesAsync();
        }
        var service = new KmQueueQueryService(factory,
            Options.Create(new KmIntegrationOptions { Enabled = true, MaxRetryCount = 8 }),
            Options.Create(new KmDeletionOptions { Enabled = true, EquipmentEnabled = true }));

        var unfinished = await service.GetIngestionAsync(KmQueueFilters.Unfinished, 1);
        Assert.Equal(2, unfinished.Total);
        Assert.DoesNotContain(unfinished.Items, x => x.Status == nameof(KmIntegrationStatus.Completed));
        Assert.Equal("等待自動重試", unfinished.Items.Single(x => x.Id == waitingRetry.Id).RetryState);
        Assert.Equal("KM 已接收；依回報錯誤處理",
            unfinished.Items.Single(x => x.Id == callbackFailure.Id).RetryState);
        Assert.All(unfinished.Items, item => Assert.Equal("未記錄", item.UploadedBy));

        var recent = await service.GetIngestionAsync(KmQueueFilters.Completed, 1);
        Assert.Single(recent.Items);
        Assert.Equal(completed.Id, recent.Items[0].Id);
    }

    [Fact]
    public async Task VerificationAttachmentIsVisibleAndWaitsForAutomaticDispatch()
    {
        var factory = new InMemoryFactory();
        var item = Outbox(KmIntegrationStatus.Pending, Now,
            KmIntegrationEventTypes.VerificationApplicationAttachmentArchived,
            KmIntegrationSourceEntityTypes.VerificationApplicationFile);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.KmIntegrationOutbox.Add(item);
            await db.SaveChangesAsync();
        }
        var service = new KmQueueQueryService(factory,
            Options.Create(new KmIntegrationOptions { Enabled = true }),
            Options.Create(new KmDeletionOptions { Enabled = true }));
        var result = await service.GetIngestionAsync(KmQueueFilters.Unfinished, 1);
        Assert.Equal(string.Empty, result.Items[0].RetryState);
        Assert.Contains("來源檔案紀錄不存在", result.Items[0].Detail);

        service = new KmQueueQueryService(factory,
            Options.Create(new KmIntegrationOptions { Enabled = false }),
            Options.Create(new KmDeletionOptions { Enabled = true }));
        result = await service.GetIngestionAsync(KmQueueFilters.Unfinished, 1);
        Assert.Equal("KM 功能停用／等待派送", result.Items[0].RetryState);
        Assert.Contains("KM 功能停用／等待派送", result.Notice);
    }

    [Fact]
    public async Task DeletionDefaultHidesOnlyFullyCompletedCleanup()
    {
        var factory = new InMemoryFactory();
        var kmVerified = DocumentDeletionJob(
            DocumentLibraryFileDeletionStatuses.KmVerified, Now.AddMinutes(-3));
        var cleanupFailed = DocumentDeletionJob(
            DocumentLibraryFileDeletionStatuses.CleanupFailed, Now.AddMinutes(-2));
        var completed = DocumentDeletionJob(
            DocumentLibraryFileDeletionStatuses.Completed, Now.AddMinutes(-4), Now);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.DocumentLibraryFileDeletionJobs.AddRange(kmVerified, cleanupFailed, completed);
            await db.SaveChangesAsync();
        }
        var service = new KmQueueQueryService(factory,
            Options.Create(new KmIntegrationOptions { Enabled = true, MaxRetryCount = 8 }),
            Options.Create(new KmDeletionOptions { Enabled = true, EquipmentEnabled = true }));

        var unfinished = await service.GetDeletionAsync(KmQueueFilters.Unfinished, 1);
        Assert.Equal(2, unfinished.Total);
        Assert.Contains(unfinished.Items, x => x.Status == DocumentLibraryFileDeletionStatuses.KmVerified);
        Assert.Contains(unfinished.Items, x => x.RetryState == "本地清理失敗");
        Assert.All(unfinished.Items, item => Assert.Equal("原始紀錄已刪除", item.UploadedBy));
        Assert.DoesNotContain(unfinished.Items, x => x.Id == completed.Id);

        var recent = await service.GetDeletionAsync(KmQueueFilters.Completed, 1);
        Assert.Single(recent.Items);
        Assert.Equal(completed.Id, recent.Items[0].Id);
    }

    [Theory]
    [InlineData(KmIntegrationSourceEntityTypes.TestReport)]
    [InlineData(KmIntegrationSourceEntityTypes.DocumentLibraryFile)]
    [InlineData(KmIntegrationSourceEntityTypes.VerificationApplicationFile)]
    [InlineData(KmIntegrationSourceEntityTypes.VendorFile)]
    [InlineData(KmIntegrationSourceEntityTypes.ApparatusFile)]
    public async Task IngestionHidesOnlySourceMissingAfterCompletedSafeDeletion(string sourceType)
    {
        var factory = new InMemoryFactory();
        var outbox = Outbox(KmIntegrationStatus.Failed, Now, sourceType: sourceType,
            kmJobId: "km-accepted", sentAt: Now);
        object deletion;
        VerificationCaseDeletionTarget? target;
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.KmIntegrationOutbox.Add(outbox);
            (deletion, target) = AddDeletionEvidence(db, sourceType, outbox.SourceEntityId,
                "Pending");
            await db.SaveChangesAsync();
        }
        var service = Queue(factory);
        var pending = await service.GetIngestionAsync(KmQueueFilters.Failed, 1);
        Assert.Equal(1, pending.Total);
        Assert.Contains("未找到已完成的安全刪除紀錄", Assert.Single(pending.Items).Detail);

        await using (var db = await factory.CreateDbContextAsync())
        {
            Set(deletion, "Status", "Completed");
            if (target is not null) Set(target, "Status", "Completed");
            db.Update(deletion);
            if (target is not null) db.Update(target);
            await db.SaveChangesAsync();
        }
        var hidden = await service.GetIngestionAsync(KmQueueFilters.Failed, 1);
        Assert.Equal(0, hidden.Total);
        Assert.Equal(0, hidden.Stats.Failed);
        Assert.Empty(hidden.Items);
        var deletionQueue = await service.GetDeletionAsync(KmQueueFilters.Completed, 1);
        Assert.NotEmpty(deletionQueue.Items);
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.Single(await db.KmIntegrationOutbox.ToListAsync());
            AddSource(db, sourceType, outbox.SourceEntityId);
            await db.SaveChangesAsync();
        }
        var present = await service.GetIngestionAsync(KmQueueFilters.Failed, 1);
        Assert.Equal(1, present.Total);
        Assert.DoesNotContain("來源檔案紀錄不存在", Assert.Single(present.Items).Detail ?? "");
    }

    [Fact]
    public async Task IngestionCountsAndPagesExcludeCompletedDeletionBeforePagination()
    {
        var factory = new InMemoryFactory();
        var hiddenFailed = Outbox(KmIntegrationStatus.Failed, Now.AddMinutes(-5),
            sourceType: KmIntegrationSourceEntityTypes.DocumentLibraryFile);
        var hiddenCompleted = Outbox(KmIntegrationStatus.Completed, Now.AddMinutes(-4),
            sourceType: KmIntegrationSourceEntityTypes.DocumentLibraryFile, completedAt: Now);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.KmIntegrationOutbox.AddRange(hiddenFailed, hiddenCompleted);
            AddDeletionEvidence(db, KmIntegrationSourceEntityTypes.DocumentLibraryFile,
                hiddenFailed.SourceEntityId, "Completed");
            AddDeletionEvidence(db, KmIntegrationSourceEntityTypes.DocumentLibraryFile,
                hiddenCompleted.SourceEntityId, "Completed");
            db.KmIntegrationOutbox.Add(Outbox(KmIntegrationStatus.Pending, Now));
            db.KmIntegrationOutbox.Add(Outbox(KmIntegrationStatus.Dispatching, Now));
            db.KmIntegrationOutbox.Add(Outbox(KmIntegrationStatus.Failed, Now));
            db.KmIntegrationOutbox.Add(Outbox(KmIntegrationStatus.Completed, Now,
                completedAt: Now));
            for (var i = 0; i < 50; i++)
                db.KmIntegrationOutbox.Add(Outbox(KmIntegrationStatus.Pending,
                    Now.AddSeconds(i + 1)));
            await db.SaveChangesAsync();
        }
        var service = Queue(factory);
        var first = await service.GetIngestionAsync(KmQueueFilters.Unfinished, 1);
        var second = await service.GetIngestionAsync(KmQueueFilters.Unfinished, 2);
        Assert.Equal(53, first.Total);
        Assert.Equal(50, first.Items.Count);
        Assert.Equal(3, second.Items.Count);
        Assert.DoesNotContain(first.Items.Concat(second.Items), x => x.Id == hiddenFailed.Id);
        Assert.Equal(new KmQueueStatsDto(53, 51, 1, 1, 1), first.Stats);
        Assert.Equal(51, (await service.GetIngestionAsync(KmQueueFilters.Waiting, 1)).Total);
        Assert.Equal(1, (await service.GetIngestionAsync(KmQueueFilters.Processing, 1)).Total);
        Assert.Equal(1, (await service.GetIngestionAsync(KmQueueFilters.Failed, 1)).Total);
        Assert.Equal(1, (await service.GetIngestionAsync(KmQueueFilters.Completed, 1)).Total);
        await using var auditDb = await factory.CreateDbContextAsync();
        Assert.Equal(56, await auditDb.KmIntegrationOutbox.CountAsync());
    }

    [Theory]
    [InlineData(KmIntegrationSourceEntityTypes.TestReport)]
    [InlineData(KmIntegrationSourceEntityTypes.VerificationApplicationFile)]
    public async Task CompletedCaseCleanupHidesSourceWithoutKmDeletionDispatch(string sourceType)
    {
        var factory = new InMemoryFactory();
        var outbox = Outbox(KmIntegrationStatus.Failed, Now, sourceType: sourceType);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.KmIntegrationOutbox.Add(outbox);
            var (job, target) = AddDeletionEvidence(db, sourceType, outbox.SourceEntityId,
                "NotDispatched");
            Set(job, "Status", "Completed");
            Assert.NotNull(target);
            await db.SaveChangesAsync();
        }
        Assert.Equal(0, (await Queue(factory).GetIngestionAsync(KmQueueFilters.Failed, 1)).Total);
    }

    [Fact]
    public void AcceptedFailedKmJobCannotUsePreAcceptanceManualRetry()
    {
        var row = Outbox(KmIntegrationStatus.Failed, Now,
            kmJobId: "km-accepted", sentAt: Now);
        Assert.False(row.CanManualRetry(Now));
        Assert.Throws<InvalidOperationException>(() => row.RequeueManually(Now));
        Assert.Throws<InvalidOperationException>(() => row.CallbackProcessing(Now));
        Assert.Throws<InvalidOperationException>(() => row.CallbackCompleted(Now));
    }

    private static KmQueueQueryService Queue(InMemoryFactory factory) => new(factory,
        Options.Create(new KmIntegrationOptions { Enabled = true, MaxRetryCount = 8 }),
        Options.Create(new KmDeletionOptions { Enabled = true, EquipmentEnabled = true }));

    private static (object Job, VerificationCaseDeletionTarget? Target) AddDeletionEvidence(
        AppDbContext db, string type, Guid id, string status)
    {
        if (type is KmIntegrationSourceEntityTypes.TestReport or
            KmIntegrationSourceEntityTypes.VerificationApplicationFile)
        {
            var job = (VerificationCaseDeletionJob)Activator.CreateInstance(
                typeof(VerificationCaseDeletionJob), nonPublic: true)!;
            Set(job, nameof(VerificationCaseDeletionJob.Id), Guid.NewGuid());
            Set(job, nameof(VerificationCaseDeletionJob.ApplicationId), Guid.NewGuid());
            Set(job, nameof(VerificationCaseDeletionJob.RecordId), Guid.NewGuid());
            Set(job, nameof(VerificationCaseDeletionJob.RequestedBy), "tester");
            Set(job, nameof(VerificationCaseDeletionJob.Reason), "Safe deletion test");
            Set(job, nameof(VerificationCaseDeletionJob.RequestedAt), Now);
            Set(job, nameof(VerificationCaseDeletionJob.Status), status);
            Set(job, nameof(VerificationCaseDeletionJob.UpdatedAt), Now);
            var target = (VerificationCaseDeletionTarget)Activator.CreateInstance(
                typeof(VerificationCaseDeletionTarget), nonPublic: true)!;
            Set(target, nameof(VerificationCaseDeletionTarget.Id), Guid.NewGuid());
            Set(target, nameof(VerificationCaseDeletionTarget.JobId), job.Id);
            Set(target, nameof(VerificationCaseDeletionTarget.SourceType),
                type == KmIntegrationSourceEntityTypes.TestReport
                    ? KmIntegrationSourceEntityTypes.TestReport
                    : KmAnalysisSourceTypes.VerificationApplicationAttachment);
            Set(target, nameof(VerificationCaseDeletionTarget.SourceEntityId), (Guid?)id);
            Set(target, nameof(VerificationCaseDeletionTarget.Status), status);
            Set(target, nameof(VerificationCaseDeletionTarget.RequestedAt), Now);
            Set(target, nameof(VerificationCaseDeletionTarget.UpdatedAt), Now);
            db.VerificationCaseDeletionJobs.Add(job);
            db.VerificationCaseDeletionTargets.Add(target);
            return (job, target);
        }
        if (type == KmIntegrationSourceEntityTypes.DocumentLibraryFile)
        {
            var job = DocumentDeletionJob(status, Now, status == "Completed" ? Now : null);
            Set(job, nameof(DocumentLibraryFileDeletionJob.DocumentFileId), id);
            db.DocumentLibraryFileDeletionJobs.Add(job);
            return (job, null);
        }
        if (type == KmIntegrationSourceEntityTypes.VendorFile)
        {
            var job = (VendorFileDeletionJob)Activator.CreateInstance(
                typeof(VendorFileDeletionJob), nonPublic: true)!;
            Set(job, nameof(VendorFileDeletionJob.Id), Guid.NewGuid());
            Set(job, nameof(VendorFileDeletionJob.VendorFileId), id);
            Set(job, nameof(VendorFileDeletionJob.RequestedAt), Now);
            Set(job, nameof(VendorFileDeletionJob.Status), status);
            db.VendorFileDeletionJobs.Add(job);
            return (job, null);
        }
        if (type == KmIntegrationSourceEntityTypes.ApparatusFile)
        {
            var job = (EquipmentFileDeletionJob)Activator.CreateInstance(
                typeof(EquipmentFileDeletionJob), nonPublic: true)!;
            Set(job, nameof(EquipmentFileDeletionJob.Id), Guid.NewGuid());
            Set(job, nameof(EquipmentFileDeletionJob.ApparatusFileId), id);
            Set(job, nameof(EquipmentFileDeletionJob.RequestedAt), Now);
            Set(job, nameof(EquipmentFileDeletionJob.Status), status);
            db.EquipmentFileDeletionJobs.Add(job);
            return (job, null);
        }
        throw new ArgumentOutOfRangeException(nameof(type));
    }

    private static void AddSource(AppDbContext db, string type, Guid id)
    {
        switch (type)
        {
            case KmIntegrationSourceEntityTypes.TestReport:
                db.ModuleCaseFiles.Add(new ModuleCaseFile { Id = id, RecordId = Guid.NewGuid(),
                    DocumentId = Guid.NewGuid(), VersionNo = 1, FileName = "report.pdf" });
                break;
            case KmIntegrationSourceEntityTypes.DocumentLibraryFile:
                db.DocumentLibraryFiles.Add(new DocumentLibraryFile { Id = id,
                    FolderId = Guid.NewGuid(), OriginalFileName = "library.pdf" });
                break;
            case KmIntegrationSourceEntityTypes.VerificationApplicationFile:
                db.VerificationApplicationFiles.Add(new VerificationApplicationFile { Id = id,
                    VerificationApplicationId = Guid.NewGuid(), FileName = "verify.pdf" });
                break;
            case KmIntegrationSourceEntityTypes.VendorFile:
                db.VendorFiles.Add(new VendorFile { Id = id, VendorId = Guid.NewGuid(),
                    OriginalFileName = "vendor.pdf" });
                break;
            case KmIntegrationSourceEntityTypes.ApparatusFile:
                db.ApparatusFiles.Add(new ApparatusFile { Id = id, ApparatusId = "EQ-TEST",
                    FileName = "equipment.pdf" });
                break;
        }
    }

    private static ClaimsPrincipal User(string account, string? role, bool csitStaff)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, account), new("account", account)
        };
        if (role is not null) claims.Add(new Claim(ClaimTypes.Role, role));
        if (csitStaff) claims.Add(new Claim(SystemAuthorization.AccessScopeClaim,
            SystemAuthorization.AccessScopes.CsitStaff));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static async Task<bool> IsAuthorizedAsync(
        IAuthorizationHandler handler, ClaimsPrincipal user)
    {
        var requirement = new KmQueueReadRequirement();
        var context = new AuthorizationHandlerContext([requirement], user, null);
        await handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    private static KmIntegrationOutbox Outbox(KmIntegrationStatus status, DateTime createdAt,
        string eventType = "TestReportCommitted", string sourceType = "TestReport",
        DateTime? nextRetryAt = null, string? kmJobId = null, DateTime? sentAt = null,
        DateTime? completedAt = null)
    {
        var item = (KmIntegrationOutbox)Activator.CreateInstance(
            typeof(KmIntegrationOutbox), nonPublic: true)!;
        Set(item, nameof(KmIntegrationOutbox.Id), Guid.NewGuid());
        Set(item, nameof(KmIntegrationOutbox.EventId), Guid.NewGuid());
        Set(item, nameof(KmIntegrationOutbox.EventType), eventType);
        Set(item, nameof(KmIntegrationOutbox.SourceSystem), "CSIT");
        Set(item, nameof(KmIntegrationOutbox.SourceEntityType), sourceType);
        Set(item, nameof(KmIntegrationOutbox.SourceEntityId), Guid.NewGuid());
        Set(item, nameof(KmIntegrationOutbox.DocumentId), Guid.NewGuid());
        Set(item, nameof(KmIntegrationOutbox.Version), 1);
        Set(item, nameof(KmIntegrationOutbox.FileName), "queue-test.pdf");
        Set(item, nameof(KmIntegrationOutbox.ContentType), "application/pdf");
        Set(item, nameof(KmIntegrationOutbox.SizeBytes), 128L);
        Set(item, nameof(KmIntegrationOutbox.CommittedAt), createdAt);
        Set(item, nameof(KmIntegrationOutbox.Status), status);
        Set(item, nameof(KmIntegrationOutbox.CreatedAt), createdAt);
        Set(item, nameof(KmIntegrationOutbox.UpdatedAt), createdAt);
        Set(item, nameof(KmIntegrationOutbox.NextRetryAt), nextRetryAt);
        Set(item, nameof(KmIntegrationOutbox.KmJobId), kmJobId);
        Set(item, nameof(KmIntegrationOutbox.SentAt), sentAt);
        Set(item, nameof(KmIntegrationOutbox.CompletedAt), completedAt);
        return item;
    }

    private static DocumentLibraryFileDeletionJob DocumentDeletionJob(
        string status, DateTime requestedAt, DateTime? completedAt = null)
    {
        var item = (DocumentLibraryFileDeletionJob)Activator.CreateInstance(
            typeof(DocumentLibraryFileDeletionJob), nonPublic: true)!;
        Set(item, nameof(DocumentLibraryFileDeletionJob.Id), Guid.NewGuid());
        Set(item, nameof(DocumentLibraryFileDeletionJob.DocumentFileId), Guid.NewGuid());
        Set(item, nameof(DocumentLibraryFileDeletionJob.FileName), "delete-test.pdf");
        Set(item, nameof(DocumentLibraryFileDeletionJob.RequestedBy), "tester");
        Set(item, nameof(DocumentLibraryFileDeletionJob.RequestedAt), requestedAt);
        Set(item, nameof(DocumentLibraryFileDeletionJob.Status), status);
        Set(item, nameof(DocumentLibraryFileDeletionJob.CompletedAt), completedAt);
        Set(item, nameof(DocumentLibraryFileDeletionJob.UpdatedAt), completedAt ?? requestedAt);
        return item;
    }

    private static void Set<T>(object item, string property, T value) =>
        item.GetType().GetProperty(property)!.SetValue(item, value);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "SIT.DepartmentSystem.Web.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class InMemoryFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> options =
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        public AppDbContext CreateDbContext() => new(options);
        public Task<AppDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
