using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace ApprovalFramework.Tests;

public sealed class EquipmentFileKmDeletionTests
{
    [Fact]
    public async Task PhotoAndLegacyFileWithoutOutboxAreDeletedLocallyWithPersistedAudit()
    {
        var factory = new Factory();
        var root = NewRoot();
        try
        {
            var photo = await SeedFileAsync(factory, root, "photo.jpg", "image/jpeg", false);
            var legacy = await SeedFileAsync(factory, root, "legacy.pdf", "application/pdf", false,
                "EQ-LEGACY");
            await using (var db = factory.CreateDbContext())
            {
                var service = new EquipmentFileDeletionService(db);
                Assert.True((await service.RequestAsync(photo, "writer"))!.CompletedLocally);
                Assert.True((await service.RequestAsync(legacy, "writer"))!.CompletedLocally);
            }
            await using var verify = factory.CreateDbContext();
            Assert.False(await verify.ApparatusFiles.AnyAsync(x => x.Id == photo || x.Id == legacy));
            Assert.Equal(2, await verify.EquipmentFileDeletionJobs.CountAsync(x =>
                x.Status == EquipmentFileDeletionStatuses.Completed && x.RequestedBy == "writer"));
            Assert.Empty(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PristinePendingOutboxIsCancelledAtomicallyBeforeLocalDeletion()
    {
        var factory = new Factory();
        var root = NewRoot();
        try
        {
            var fileId = await SeedFileAsync(factory, root, "manual.pdf", "application/pdf", true);
            await using var db = factory.CreateDbContext();
            var result = await new EquipmentFileDeletionService(db).RequestAsync(fileId, "writer");
            Assert.True(result!.CompletedLocally);
            Assert.False(await db.ApparatusFiles.AnyAsync(x => x.Id == fileId));
            Assert.False(await db.KmIntegrationOutbox.AnyAsync(x => x.SourceEntityId == fileId));
            Assert.Equal(EquipmentFileDeletionStatuses.Completed,
                (await db.EquipmentFileDeletionJobs.SingleAsync(x => x.ApparatusFileId == fileId)).Status);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ActiveIngestionLeaseCannotBeMistakenForNeverDelivered()
    {
        var factory = new Factory();
        var root = NewRoot();
        try
        {
            var fileId = await SeedFileAsync(factory, root, "leased.pdf", "application/pdf", true);
            Guid ingestionLease;
            await using (var db = factory.CreateDbContext())
            {
                var outbox = await db.KmIntegrationOutbox.SingleAsync(x => x.SourceEntityId == fileId);
                ingestionLease = Guid.NewGuid();
                outbox.Claim(ingestionLease, DateTime.UtcNow, TimeSpan.FromMinutes(5));
                await db.SaveChangesAsync();
            }
            await using (var db = factory.CreateDbContext())
            {
                var result = await new EquipmentFileDeletionService(db).RequestAsync(fileId, "writer");
                Assert.False(result!.CompletedLocally);
                Assert.Equal(EquipmentFileDeletionStatuses.Pending, result.Status);
                Assert.True(await db.ApparatusFiles.AnyAsync(x => x.Id == fileId));
            }
            var adapter = new MockEquipmentDeletionApi();
            Assert.Equal(0, await Dispatcher(factory, adapter).ProcessBatchAsync());
            Assert.Empty(adapter.Requests);

            await using (var db = factory.CreateDbContext())
            {
                var outbox = await db.KmIntegrationOutbox.SingleAsync(x => x.SourceEntityId == fileId);
                outbox.DeliveryFailed(ingestionLease, "CANCELLED_BY_DELETION",
                    "Equipment deletion took ownership.", null, DateTime.UtcNow);
                await db.SaveChangesAsync();
            }
            Assert.Equal(1, await Dispatcher(factory, adapter).ProcessBatchAsync());
            Assert.Single(adapter.Requests);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MockAcceptedCompletedCallbackDeletesOnlyCorrelatedFileAndIsIdempotent()
    {
        var factory = new Factory();
        var root = NewRoot();
        try
        {
            var targetId = await SeedSentFileAsync(factory, root, "target.pdf", "EQ-TARGET");
            var otherId = await SeedSentFileAsync(factory, root, "other.pdf", "EQ-OTHER");
            EquipmentFileDeletionRequestResult requested;
            await using (var db = factory.CreateDbContext())
                requested = (await new EquipmentFileDeletionService(db)
                    .RequestAsync(targetId, "writer"))!;
            Assert.False(requested.CompletedLocally);

            var adapter = new MockEquipmentDeletionApi();
            Assert.Equal(1, await Dispatcher(factory, adapter).ProcessBatchAsync());
            var accepted = Assert.Single(adapter.Requests);
            Assert.Equal(targetId, accepted.Resource.ApparatusFileId);
            Assert.Equal("ApparatusFile", accepted.Resource.SourceEntityType);

            await using (var before = factory.CreateDbContext())
            {
                Assert.True(await before.ApparatusFiles.AnyAsync(x => x.Id == targetId));
                Assert.True(File.Exists(Path.Combine(root, "target.pdf")));
            }

            var callback = Completed(accepted, adapter.JobId(accepted.DeletionRequestId));
            var callbacks = new EquipmentFileDeletionCallbackService(factory);
            Assert.Equal(KmDeletionCallbackOutcome.Accepted, await callbacks.ApplyAsync(callback));
            Assert.Equal(KmDeletionCallbackOutcome.Accepted, await callbacks.ApplyAsync(callback));
            Assert.Equal(KmDeletionCallbackOutcome.Conflict,
                await callbacks.ApplyAsync(callback with { CallbackId = Guid.NewGuid() }));

            await using var verify = factory.CreateDbContext();
            Assert.False(await verify.ApparatusFiles.AnyAsync(x => x.Id == targetId));
            Assert.True(await verify.ApparatusFiles.AnyAsync(x => x.Id == otherId));
            Assert.False(File.Exists(Path.Combine(root, "target.pdf")));
            Assert.True(File.Exists(Path.Combine(root, "other.pdf")));
            var job = await verify.EquipmentFileDeletionJobs.SingleAsync(x =>
                x.ApparatusFileId == targetId);
            Assert.Equal(EquipmentFileDeletionStatuses.Completed, job.Status);
            Assert.NotNull(job.KmVerifiedAt);
            Assert.NotNull(job.DbRecordDeletedAt);
            Assert.Single(await verify.EquipmentFileDeletionCallbacks.ToListAsync());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DeliveryAndCallbackFailuresRemainPersistedAndRetryWithoutEarlyCleanup()
    {
        var factory = new Factory();
        var root = NewRoot();
        try
        {
            var fileId = await SeedSentFileAsync(factory, root, "retry.pdf", "EQ-RETRY");
            await using (var db = factory.CreateDbContext())
                await new EquipmentFileDeletionService(db).RequestAsync(fileId, "writer");
            var adapter = new MockEquipmentDeletionApi(
                KmDeletionDeliveryResult.Failure(true, "TIMEOUT"),
                KmDeletionDeliveryResult.Success("KM-EQUIPMENT-RETRY"));
            Assert.Equal(0, await Dispatcher(factory, adapter).ProcessBatchAsync());
            await using (var failed = factory.CreateDbContext())
            {
                var job = await failed.EquipmentFileDeletionJobs.SingleAsync();
                Assert.Equal(EquipmentFileDeletionStatuses.Failed, job.Status);
                Assert.True(job.LastErrorRetryable);
                Assert.True(await failed.ApparatusFiles.AnyAsync(x => x.Id == fileId));
            }
            Assert.Equal(1, await Dispatcher(factory, adapter).ProcessBatchAsync());
            var request = adapter.Requests.Last();
            var failedCallback = Failed(request, "KM-EQUIPMENT-RETRY");
            var callbacks = new EquipmentFileDeletionCallbackService(factory);
            Assert.Equal(KmDeletionCallbackOutcome.Accepted,
                await callbacks.ApplyAsync(failedCallback));
            await using (var failed = factory.CreateDbContext())
                Assert.True(await failed.ApparatusFiles.AnyAsync(x => x.Id == fileId));
            Assert.Equal(1, await Dispatcher(factory, adapter).ProcessBatchAsync());
            Assert.Equal(KmDeletionCallbackOutcome.Accepted,
                await callbacks.ApplyAsync(Completed(request, "KM-EQUIPMENT-RETRY")));
            await using var completed = factory.CreateDbContext();
            Assert.False(await completed.ApparatusFiles.AnyAsync(x => x.Id == fileId));
        }
        finally { Directory.Delete(root, true); }
    }

    private static KmDeletionDispatchService Dispatcher(Factory factory,
        IEquipmentDocumentDeletionAdapter adapter) => new(factory, new NoopCaseDeletionClient(),
        Options.Create(new KmDeletionOptions
        { Enabled = true, EquipmentEnabled = true, Scope = "mock.equipment.delete" }),
        Options.Create(new KmIntegrationOptions
        {
            BaseUrl = "https://km.mock.test/", TimeoutSeconds = 2, LeaseSeconds = 30,
            MaxRetryCount = 4
        }), adapter);

    private static EquipmentDocumentDeletionCallback Completed(
        EquipmentDocumentDeletionRequest request, string jobId)
    {
        var now = DateTimeOffset.UtcNow;
        return new("CSIT.EquipmentDocumentDeletion", "draft-1", Guid.NewGuid(), "CSIT",
            request.DeletionRequestId, jobId, request.Resource, "Completed",
            new KmDeletionTimestamps(now, now.AddSeconds(2), null),
            new KmDeletionResult(true, now.AddSeconds(1),
                new KmDeletionCleanup("Deleted", "Deleted", "Deleted", "Deleted",
                    "Deleted", "Deleted", "NotPresent")), null);
    }

    private static EquipmentDocumentDeletionCallback Failed(
        EquipmentDocumentDeletionRequest request, string jobId)
    {
        var now = DateTimeOffset.UtcNow;
        return new("CSIT.EquipmentDocumentDeletion", "draft-1", Guid.NewGuid(), "CSIT",
            request.DeletionRequestId, jobId, request.Resource, "Failed",
            new KmDeletionTimestamps(now, null, now.AddSeconds(1)), null,
            new KmDeletionError("KM_TEMPORARY", "Synthetic retryable failure.", true));
    }

    private static async Task<Guid> SeedSentFileAsync(Factory factory, string root, string name,
        string equipmentId)
    {
        var id = await SeedFileAsync(factory, root, name, "application/pdf", true, equipmentId);
        await using var db = factory.CreateDbContext();
        var outbox = await db.KmIntegrationOutbox.SingleAsync(x => x.SourceEntityId == id);
        var lease = Guid.NewGuid();
        outbox.Claim(lease, DateTime.UtcNow, TimeSpan.FromMinutes(5));
        outbox.Sent(lease, "INGEST-" + id.ToString("N"), DateTime.UtcNow);
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<Guid> SeedFileAsync(Factory factory, string root, string name,
        string contentType, bool withOutbox, string equipmentId = "EQ-DELETE")
    {
        var id = Guid.NewGuid();
        await File.WriteAllTextAsync(Path.Combine(root, name), "equipment-content-" + id);
        var info = new FileInfo(Path.Combine(root, name));
        var equipment = new Apparatus
        {
            Id = equipmentId, ModuleCode = "equipment", Name = equipmentId, Kind = "Tester",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        var file = new ApparatusFile
        {
            Id = id, ApparatusId = equipmentId, Apparatus = equipment,
            FileName = name, OriginalFileName = name, FilePath = root,
            ContentType = contentType, FileSize = info.Length,
            Sha256 = new string('a', 64), ArchiveStatus = FileArchiveStatuses.Archived,
            ArchiveUpdatedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow
        };
        await using var db = factory.CreateDbContext();
        db.Apparatuses.Add(equipment);
        db.ApparatusFiles.Add(file);
        if (withOutbox)
            db.KmIntegrationOutbox.Add(KmIntegrationOutbox.ForArchivedEquipmentDocument(
                equipment, file, DateTime.UtcNow));
        await db.SaveChangesAsync();
        return id;
    }

    private static string NewRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "csit-equipment-delete-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class MockEquipmentDeletionApi(params KmDeletionDeliveryResult[] results)
        : IEquipmentDocumentDeletionAdapter
    {
        private readonly Queue<KmDeletionDeliveryResult> queued = new(results);
        private readonly Dictionary<Guid, string> jobs = [];
        public List<EquipmentDocumentDeletionRequest> Requests { get; } = [];
        public string JobId(Guid requestId) => jobs[requestId];
        public Task<KmDeletionDeliveryResult> SubmitAsync(EquipmentDocumentDeletionRequest request,
            CancellationToken cancellationToken = default)
        {
            Assert.True(request.IsValid());
            Requests.Add(request);
            if (queued.TryDequeue(out var configured))
            {
                if (configured.Accepted) jobs[request.DeletionRequestId] = configured.JobId!;
                return Task.FromResult(configured);
            }
            if (!jobs.TryGetValue(request.DeletionRequestId, out var jobId))
            {
                jobId = "KM-EQUIPMENT-" + request.DeletionRequestId.ToString("N");
                jobs[request.DeletionRequestId] = jobId;
            }
            return Task.FromResult(KmDeletionDeliveryResult.Success(jobId));
        }
    }

    private sealed class NoopCaseDeletionClient : IKmDeletionClient
    {
        public Task<KmDeletionDeliveryResult> SubmitAsync(KmDeletionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Verification Case deletion must not be dispatched.");
    }

    private sealed class Factory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> options =
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        public AppDbContext CreateDbContext() => new(options);
    }
}
