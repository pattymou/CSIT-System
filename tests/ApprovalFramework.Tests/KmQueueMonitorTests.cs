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
    private static readonly DateTime Now =
        new(2026, 11, 3, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void QueueDtoDoesNotExposeStoragePathsAndControllerRequiresDedicatedPolicy()
    {
        Assert.DoesNotContain(typeof(KmQueueItemDto).GetProperties(), property =>
            property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase));
        var authorize = typeof(KmQueueController).GetCustomAttribute<AuthorizeAttribute>();
        Assert.Equal(SystemAuthorization.Policies.KmQueueRead, authorize?.Policy);
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
        Assert.Equal("不可自動重試／需要人工處理",
            unfinished.Items.Single(x => x.Id == callbackFailure.Id).RetryState);
        Assert.All(unfinished.Items, item => Assert.Equal("未記錄", item.UploadedBy));

        var recent = await service.GetIngestionAsync(KmQueueFilters.Completed, 1);
        Assert.Single(recent.Items);
        Assert.Equal(completed.Id, recent.Items[0].Id);
    }

    [Fact]
    public async Task UnsupportedVerificationAttachmentRemainsVisibleForManualHandling()
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
        Assert.Equal("不可自動重試／需要人工處理", result.Items[0].RetryState);
        Assert.Contains("不支援", result.Items[0].Detail);
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
