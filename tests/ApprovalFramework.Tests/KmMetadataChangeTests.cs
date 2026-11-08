using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace ApprovalFramework.Tests;

public sealed class KmMetadataChangeTests
{
    [Fact]
    public async Task VerifiedSnapshot_DispatchesAndCompletesWithExactRevisionAndIdempotency()
    {
        var factory = new Factory();
        var resultId = await SeedVerifiedDocumentAsync(factory);
        var query = new KmAnalysisQueryService(factory);
        var queue = new KmMetadataChangeQueueService(factory, query);
        var request = new KmMetadataChangeQueueRequest(
            new KmV2DocumentClassification(null, null, null),
            new KmMetadataAttributes(["wifi"], [], "guide.pdf", [], null));
        var queued = await queue.QueueAsync(Admin(), resultId, request);
        Assert.Equal(2, queued.SourceRevision);
        Assert.Equal(["classification", "attributes.tags"], queued.ChangedFields);

        await using var mock = await MockKmIngestionServer.StartAsync();
        var options = Options();
        var worker = new KmMetadataChangeWorker(factory,
            new KmIntegrationClient(mock.Client, Microsoft.Extensions.Options.Options.Create(options),
                new TokenProvider()), Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<KmMetadataChangeWorker>.Instance);
        Assert.Equal(1, await worker.ProcessBatchAsync());
        Assert.Equal(1, mock.MetadataChangeRequestCount);
        var outbound = Assert.IsType<KmMetadataChangeRequest>(mock.LastMetadataChangeRequest);
        Assert.Equal(queued.ChangeId, outbound.ChangeId);
        Assert.Equal(2, outbound.SourceRevision);
        Assert.Equal(["classification", "attributes.tags"], outbound.ChangedFields);

        var callbacks = new KmMetadataChangeCallbackService(factory,
            Microsoft.Extensions.Options.Options.Create(options));
        var started = new KmMetadataChangeStatusCallback("2.0", Guid.NewGuid(), queued.ChangeId,
            mock.JobId, "CSIT", "Processing", 1,
            new KmV2CallbackTimestamps(DateTimeOffset.UtcNow, null, null), null);
        await callbacks.ApplyAsync(started);
        await callbacks.ApplyAsync(started);

        var wrongRevision = new KmMetadataChangeStatusCallback("2.0", Guid.NewGuid(), queued.ChangeId,
            mock.JobId, "CSIT", "Completed", 1,
            new KmV2CallbackTimestamps(null, DateTimeOffset.UtcNow, null), null);
        var conflict = await Assert.ThrowsAsync<KmAnalysisCallbackException>(
            () => callbacks.ApplyAsync(wrongRevision));
        Assert.Equal(409, conflict.StatusCode);
        Assert.Equal("revision_conflict", conflict.Error);

        var completed = wrongRevision with { CallbackId = Guid.NewGuid(), AppliedRevision = 2 };
        await callbacks.ApplyAsync(completed);
        await callbacks.ApplyAsync(completed);
        await using var db = await factory.CreateDbContextAsync();
        var persisted = await db.KmMetadataChanges.SingleAsync();
        Assert.Equal(KmMetadataChangeStatuses.Completed, persisted.Status);
        Assert.Equal(mock.JobId, persisted.KmChangeJobId);
        Assert.Equal(2, await db.KmMetadataChangeCallbacks.CountAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => queue.QueueAsync(Admin(), resultId, request));
    }

    [Fact]
    public async Task UnverifiedResult_CannotCreateMetadataChange()
    {
        var factory = new Factory();
        var resultId = await SeedVerifiedDocumentAsync(factory, verify: false);
        var queue = new KmMetadataChangeQueueService(factory, new KmAnalysisQueryService(factory));
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.QueueAsync(Admin(), resultId,
            new KmMetadataChangeQueueRequest(new(null, null, null),
                new([], [], "changed", [], null))));
        await using var db = await factory.CreateDbContextAsync();
        Assert.Empty(await db.KmMetadataChanges.ToListAsync());
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task VerifiedMetadataChange_RunsAgainstIsolatedPostgreSql()
    {
        if (!KmMockE2EPostgreSql.TryGetConnection(
                "CSIT_TASKC_POSTGRES_CONNECTION",
                ["csit_taskc_e2e_"], [55432], out var connection)) return;

        var factory = new PostgreSqlFactory(connection);
        var resultId = await SeedVerifiedDocumentAsync(factory);
        var queue = new KmMetadataChangeQueueService(factory, new KmAnalysisQueryService(factory));
        var queued = await queue.QueueAsync(Admin(), resultId, new KmMetadataChangeQueueRequest(
            new KmV2DocumentClassification(null, null, null),
            new KmMetadataAttributes(["postgres"], [], "guide.pdf", [], null)));
        await using var mock = await MockKmIngestionServer.StartAsync();
        var options = Options();
        var worker = new KmMetadataChangeWorker(factory,
            new KmIntegrationClient(mock.Client, Microsoft.Extensions.Options.Options.Create(options),
                new TokenProvider()), Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<KmMetadataChangeWorker>.Instance);
        Assert.Equal(1, await worker.ProcessBatchAsync());

        var callbacks = new KmMetadataChangeCallbackService(factory,
            Microsoft.Extensions.Options.Options.Create(options));
        await callbacks.ApplyAsync(new KmMetadataChangeStatusCallback(
            "2.0", Guid.NewGuid(), queued.ChangeId, mock.JobId, "CSIT", "Processing", 1,
            new KmV2CallbackTimestamps(DateTimeOffset.UtcNow, null, null), null));
        var completed = new KmMetadataChangeStatusCallback(
            "2.0", Guid.NewGuid(), queued.ChangeId, mock.JobId, "CSIT", "Completed",
            queued.SourceRevision, new KmV2CallbackTimestamps(null, DateTimeOffset.UtcNow, null), null);
        await callbacks.ApplyAsync(completed);
        await callbacks.ApplyAsync(completed);

        await using var db = await factory.CreateDbContextAsync();
        var persisted = await db.KmMetadataChanges.SingleAsync(x => x.ChangeId == queued.ChangeId);
        Assert.Equal(KmMetadataChangeStatuses.Completed, persisted.Status);
        Assert.Equal(queued.SourceRevision, persisted.SourceRevision);
        Assert.Equal(2, await db.KmMetadataChangeCallbacks.CountAsync(x =>
            x.ChangeId == queued.ChangeId));
    }

    private static async Task<Guid> SeedVerifiedDocumentAsync(
        IDbContextFactory<AppDbContext> factory, bool verify = true)
    {
        var now = DateTime.UtcNow;
        var l1 = new DocumentFolder { Id = Guid.NewGuid(), Level = 1, Name = "L1", CreatedAt = now, UpdatedAt = now };
        var l2 = new DocumentFolder { Id = Guid.NewGuid(), ParentId = l1.Id, Level = 2, Name = "L2", CreatedAt = now, UpdatedAt = now };
        var l3 = new DocumentFolder { Id = Guid.NewGuid(), ParentId = l2.Id, Level = 3, Name = "L3", CreatedAt = now, UpdatedAt = now };
        var file = new DocumentLibraryFile
        {
            Id = Guid.NewGuid(), DocumentId = Guid.NewGuid(), Version = 1, FolderId = l3.Id,
            OriginalFileName = "guide.pdf", StandardFileName = "DOC-guide.pdf",
            ContentType = "application/pdf", UploadedBy = "admin", UploadedAt = now,
            FileSize = 10, Sha256 = new string('a', 64), RelativePath = "guide.pdf",
            StorageArea = DocumentStorageAreas.Library
        };
        var job = new DocumentProcessingJob
        {
            Id = Guid.NewGuid(), DocumentFileId = file.Id, Status = DocumentProcessingStatuses.Completed,
            CreatedAt = now, UpdatedAt = now, KmCompletedAt = now,
            MetadataJson = "{}", ArchiveStatus = DocumentArchiveStatuses.Completed,
            ArchiveSha256 = file.Sha256, ArchivedAt = now
        };
        var outbox = KmIntegrationOutbox.ForDocumentLibraryFile(file, job, now);
        var lease = Guid.NewGuid();
        outbox.Claim(lease, now, TimeSpan.FromMinutes(1));
        outbox.Sent(lease, "analysis-job", now);
        outbox.CallbackCompleted(now);
        var callbackId = Guid.NewGuid();
        var callbackSha = new string('b', 64);
        var receipt = new KmAnalysisCallbackReceipt
        {
            CallbackId = callbackId, OutboxId = outbox.Id, Endpoint = "test-seed",
            Status = "Completed", PayloadSha256 = callbackSha, ReceivedAt = now
        };
        var result = KmDocumentAnalysisResult.Create(Guid.NewGuid(), callbackId, outbox,
            KmAnalysisSourceTypes.DocumentLibraryFile, file.Id, file.DocumentId, 1, outbox.Id,
            "analysis-job", file.Sha256, "1.0", "{}", now, now, now, 1,
            callbackSha);
        await using var db = await factory.CreateDbContextAsync();
        db.AddRange(l1, l2, l3, file, job, outbox, receipt, result);
        await db.SaveChangesAsync();
        if (verify)
        {
            var revision = result.Review(KmAnalysisReviewStatuses.Verified, "{}", null, "admin", now);
            db.Entry(revision).State = EntityState.Added;
            await db.SaveChangesAsync();
        }
        return result.Id;
    }

    private static ClaimsPrincipal Admin() => new(new ClaimsIdentity(
        [new Claim("account", "admin"), new Claim(ClaimTypes.Role, "Admin")], "test"));
    private static KmIntegrationOptions Options() => new()
    {
        Enabled = true, BaseUrl = "https://mock-km.test/", ServiceBaseUrl = "https://mock-csit.test/",
        TimeoutSeconds = 2, LeaseSeconds = 8, MaxRetryCount = 3,
        RetryScheduleSeconds = [1], PollSeconds = 1
    };
    private sealed class TokenProvider : IKmIntegrationAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) =>
            Task.FromResult("mock-csit-token");
    }
    private sealed class Factory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> options =
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        public AppDbContext CreateDbContext() => new(options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class PostgreSqlFactory(string connection) : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> options =
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options;
        public AppDbContext CreateDbContext() => new(options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
