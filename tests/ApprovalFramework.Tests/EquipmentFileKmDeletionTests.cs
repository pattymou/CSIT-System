using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace ApprovalFramework.Tests;

public sealed class EquipmentFileKmDeletionTests
{
    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task ImageDeletionSafetyPaths_RunAgainstIsolatedPostgreSql()
    {
        if (!KmMockE2EPostgreSql.TryGetConnection(
                "CSIT_EQUIPMENT_DELETION_POSTGRES_CONNECTION",
                ["csit_equipment_deletion_", "csit_task_d_"], [55434, 55435], out var connection)) return;

        var factory = new Factory(connection);
        await using (var migrate = factory.CreateDbContext())
        {
            await migrate.Database.MigrateAsync();
            Assert.Contains("20261108070415_AddKmTaskDDocumentDeletionV141",
                await migrate.Database.GetAppliedMigrationsAsync());
        }
        var root = NewRoot();
        try
        {
            var pristine = await SeedFileAsync(factory, root, "pristine.png", "image/png",
                true, "EQ-PG-PRISTINE");
            await using (var db = factory.CreateDbContext())
            {
                var result = await new EquipmentFileDeletionService(db)
                    .RequestAsync(pristine, "pg-test");
                Assert.True(result!.CompletedLocally);
                Assert.False(await db.ApparatusFiles.AnyAsync(x => x.Id == pristine));
                Assert.False(await db.KmIntegrationOutbox.AnyAsync(x =>
                    x.SourceEntityId == pristine));
            }

            var leased = await SeedFileAsync(factory, root, "leased.jpg", "image/jpeg",
                true, "EQ-PG-LEASED");
            await using (var db = factory.CreateDbContext())
            {
                var outbox = await db.KmIntegrationOutbox.SingleAsync(x =>
                    x.SourceEntityId == leased);
                outbox.Claim(Guid.NewGuid(), DateTime.UtcNow, TimeSpan.FromMinutes(5));
                await db.SaveChangesAsync();
                var result = await new EquipmentFileDeletionService(db)
                    .RequestAsync(leased, "pg-test");
                Assert.False(result!.CompletedLocally);
            }
            var adapter = new MockEquipmentDeletionApi();
            Assert.Equal(0, await Dispatcher(factory, adapter).ProcessBatchAsync());

            await using (var db = factory.CreateDbContext())
            {
                var outbox = await db.KmIntegrationOutbox.SingleAsync(x =>
                    x.SourceEntityId == leased);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE km_integration_outbox
                    SET lease_expires_at = {DateTime.UtcNow.AddMinutes(-1)}
                    WHERE id = {outbox.Id}
                    """);
            }
            Assert.Equal(1, await Dispatcher(factory, adapter).ProcessBatchAsync());
            var request = Assert.Single(adapter.Requests);
            await using (var dispatched = factory.CreateDbContext())
                Assert.Equal("CANCELLED_BY_DELETION",
                    (await dispatched.KmIntegrationOutbox.SingleAsync(x =>
                        x.SourceEntityId == leased)).LastErrorCode);
            var callback = Completed(request, adapter.JobId(request.DeletionRequestId));
            var callbacks = new KmDocumentDeletionV2CallbackService(factory);
            Assert.Equal(KmDeletionCallbackOutcome.Accepted,
                await callbacks.ApplyAsync(callback));
            Assert.Equal(KmDeletionCallbackOutcome.Accepted,
                await callbacks.ApplyAsync(callback));
            await using var verify = factory.CreateDbContext();
            Assert.False(await verify.KmIntegrationOutbox.AnyAsync(x =>
                x.SourceEntityId == leased));
            Assert.False(await verify.ApparatusFiles.AnyAsync(x => x.Id == leased));
            Assert.Equal(EquipmentFileDeletionStatuses.Completed,
                (await verify.EquipmentFileDeletionJobs.SingleAsync(x =>
                    x.ApparatusFileId == leased)).Status);
            Assert.Single(await verify.KmDocumentDeletionV2Callbacks.Where(x =>
                x.DeletionRequestId == request.DeletionRequestId).ToListAsync());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PhotoAndLegacyFileWithoutOutboxAreDeletedLocallyWithPersistedAudit()
    {
        var factory = new Factory();
        var root = NewRoot();
        try
        {
            var photo = await SeedFileAsync(factory, root, "photo.jpg", "image/jpeg", false);
            var diagram = await SeedFileAsync(factory, root, "diagram.png", "image/png", false,
                "EQ-PNG");
            var legacy = await SeedFileAsync(factory, root, "legacy.pdf", "application/pdf", false,
                "EQ-LEGACY");
            await using (var db = factory.CreateDbContext())
            {
                var service = new EquipmentFileDeletionService(db);
                Assert.True((await service.RequestAsync(photo, "writer"))!.CompletedLocally);
                Assert.True((await service.RequestAsync(diagram, "writer"))!.CompletedLocally);
                Assert.True((await service.RequestAsync(legacy, "writer"))!.CompletedLocally);
            }
            await using var verify = factory.CreateDbContext();
            Assert.False(await verify.ApparatusFiles.AnyAsync(x =>
                x.Id == photo || x.Id == diagram || x.Id == legacy));
            Assert.Equal(3, await verify.EquipmentFileDeletionJobs.CountAsync(x =>
                x.Status == EquipmentFileDeletionStatuses.Completed && x.RequestedBy == "writer"));
            Assert.Empty(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("manual.pdf", "application/pdf", KmIntegrationEventTypes.EquipmentDocumentArchived)]
    [InlineData("front.png", "image/png", KmIntegrationEventTypes.EquipmentAttachmentArchived)]
    [InlineData("photo.jpg", "image/jpeg", KmIntegrationEventTypes.EquipmentAttachmentArchived)]
    public async Task PristinePendingOutboxIsCancelledAtomicallyBeforeLocalDeletion(
        string fileName, string contentType, string expectedEventType)
    {
        var factory = new Factory();
        var root = NewRoot();
        try
        {
            var fileId = await SeedFileAsync(factory, root, fileName, contentType, true);
            await using var db = factory.CreateDbContext();
            Assert.Equal(expectedEventType,
                (await db.KmIntegrationOutbox.SingleAsync(x => x.SourceEntityId == fileId)).EventType);
            var result = await new EquipmentFileDeletionService(db).RequestAsync(fileId, "writer");
            Assert.True(result!.CompletedLocally);
            Assert.False(await db.ApparatusFiles.AnyAsync(x => x.Id == fileId));
            Assert.False(await db.KmIntegrationOutbox.AnyAsync(x => x.SourceEntityId == fileId));
            Assert.Equal(EquipmentFileDeletionStatuses.Completed,
                (await db.EquipmentFileDeletionJobs.SingleAsync(x => x.ApparatusFileId == fileId)).Status);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("leased.pdf", "application/pdf")]
    [InlineData("leased.png", "image/png")]
    [InlineData("leased.jpg", "image/jpeg")]
    public async Task ActiveIngestionLeaseCannotBeMistakenForNeverDelivered(
        string fileName, string contentType)
    {
        var factory = new Factory();
        var root = NewRoot();
        try
        {
            var fileId = await SeedFileAsync(factory, root, fileName, contentType, true);
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

    [Theory]
    [InlineData("sent.pdf", "application/pdf", KmIntegrationStatus.Sent)]
    [InlineData("processing.png", "image/png", KmIntegrationStatus.Processing)]
    [InlineData("completed.jpg", "image/jpeg", KmIntegrationStatus.Completed)]
    [InlineData("failed.jpg", "image/jpeg", KmIntegrationStatus.Failed)]
    public async Task DeliveryEvidenceAlwaysRequiresKmDeletion(
        string fileName, string contentType, KmIntegrationStatus status)
    {
        var factory = new Factory();
        var root = NewRoot();
        try
        {
            var fileId = await SeedFileAsync(factory, root, fileName, contentType, true);
            await using (var db = factory.CreateDbContext())
            {
                var outbox = await db.KmIntegrationOutbox.SingleAsync(x => x.SourceEntityId == fileId);
                var lease = Guid.NewGuid();
                var now = DateTime.UtcNow;
                outbox.Claim(lease, now, TimeSpan.FromMinutes(5));
                if (status == KmIntegrationStatus.Failed)
                    outbox.DeliveryFailed(lease, "HTTP_503", "Attempted delivery.", now.AddMinutes(1), now);
                else
                {
                    outbox.Sent(lease, "INGEST-" + fileId.ToString("N"), now);
                    if (status is KmIntegrationStatus.Processing or KmIntegrationStatus.Completed)
                        outbox.CallbackProcessing(now);
                    if (status == KmIntegrationStatus.Completed)
                        outbox.CallbackCompleted(now);
                }
                await db.SaveChangesAsync();
            }

            await using var requestDb = factory.CreateDbContext();
            var result = await new EquipmentFileDeletionService(requestDb)
                .RequestAsync(fileId, "writer");

            Assert.NotNull(result);
            Assert.False(result.CompletedLocally);
            Assert.Equal(EquipmentFileDeletionStatuses.Pending, result.Status);
            Assert.True(await requestDb.ApparatusFiles.AnyAsync(x => x.Id == fileId));
            Assert.True(await requestDb.KmIntegrationOutbox.AnyAsync(x => x.SourceEntityId == fileId));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExpiredImageIngestionLeaseIsSafelyTakenOverByDeletionDispatch()
    {
        var factory = new Factory();
        var root = NewRoot();
        try
        {
            var fileId = await SeedFileAsync(factory, root, "expired.png", "image/png", true);
            await using (var db = factory.CreateDbContext())
            {
                var outbox = await db.KmIntegrationOutbox.SingleAsync(x => x.SourceEntityId == fileId);
                outbox.Claim(Guid.NewGuid(), DateTime.UtcNow.AddMinutes(-10), TimeSpan.FromMinutes(1));
                await db.SaveChangesAsync();
            }
            await using (var db = factory.CreateDbContext())
                await new EquipmentFileDeletionService(db).RequestAsync(fileId, "writer");

            var adapter = new MockEquipmentDeletionApi();
            Assert.Equal(1, await Dispatcher(factory, adapter).ProcessBatchAsync());
            Assert.Single(adapter.Requests);

            await using var verify = factory.CreateDbContext();
            var storedOutbox = await verify.KmIntegrationOutbox.SingleAsync(x => x.SourceEntityId == fileId);
            Assert.Equal(KmIntegrationStatus.Failed, storedOutbox.Status);
            Assert.Equal("CANCELLED_BY_DELETION", storedOutbox.LastErrorCode);
            Assert.Equal(EquipmentFileDeletionStatuses.Accepted,
                (await verify.EquipmentFileDeletionJobs.SingleAsync()).Status);
            Assert.True(await verify.ApparatusFiles.AnyAsync(x => x.Id == fileId));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("target.pdf", "application/pdf")]
    [InlineData("target.png", "image/png")]
    [InlineData("target.jpg", "image/jpeg")]
    public async Task MockAcceptedCompletedCallbackDeletesOnlyCorrelatedFileAndIsIdempotent(
        string fileName, string contentType)
    {
        var factory = new Factory();
        var root = NewRoot();
        try
        {
            var targetId = await SeedSentFileAsync(factory, root, fileName, contentType, "EQ-TARGET");
            var otherId = await SeedSentFileAsync(factory, root, "other.pdf", "EQ-OTHER");
            EquipmentFileDeletionRequestResult requested;
            await using (var db = factory.CreateDbContext())
                requested = (await new EquipmentFileDeletionService(db)
                    .RequestAsync(targetId, "writer"))!;
            Assert.False(requested.CompletedLocally);
            await using (var duplicateDb = factory.CreateDbContext())
                Assert.Equal(requested.RequestId,
                    (await new EquipmentFileDeletionService(duplicateDb)
                        .RequestAsync(targetId, "writer"))!.RequestId);

            var adapter = new MockEquipmentDeletionApi();
            Assert.Equal(1, await Dispatcher(factory, adapter).ProcessBatchAsync());
            var accepted = Assert.Single(adapter.Requests);
            Assert.Equal(targetId, accepted.Resource.SourceEntityId);
            Assert.Equal("EquipmentAttachment", accepted.Resource.SourceType);

            await using (var before = factory.CreateDbContext())
            {
                Assert.True(await before.ApparatusFiles.AnyAsync(x => x.Id == targetId));
                Assert.True(File.Exists(Path.Combine(root, fileName)));
            }

            var callback = Completed(accepted, adapter.JobId(accepted.DeletionRequestId));
            var callbacks = new KmDocumentDeletionV2CallbackService(factory);
            Assert.Equal(KmDeletionCallbackOutcome.Accepted, await callbacks.ApplyAsync(callback));
            Assert.Equal(KmDeletionCallbackOutcome.Accepted, await callbacks.ApplyAsync(callback));
            Assert.Equal(KmDeletionCallbackOutcome.Conflict,
                await callbacks.ApplyAsync(callback with { CallbackId = Guid.NewGuid() }));

            await using var verify = factory.CreateDbContext();
            Assert.False(await verify.ApparatusFiles.AnyAsync(x => x.Id == targetId));
            Assert.True(await verify.ApparatusFiles.AnyAsync(x => x.Id == otherId));
            Assert.False(File.Exists(Path.Combine(root, fileName)));
            Assert.True(File.Exists(Path.Combine(root, "other.pdf")));
            var job = await verify.EquipmentFileDeletionJobs.SingleAsync(x =>
                x.ApparatusFileId == targetId);
            Assert.Equal(EquipmentFileDeletionStatuses.Completed, job.Status);
            Assert.NotNull(job.KmVerifiedAt);
            Assert.NotNull(job.DbRecordDeletedAt);
            Assert.Single(await verify.KmDocumentDeletionV2Callbacks.ToListAsync());
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
            var fileId = await SeedSentFileAsync(factory, root, "retry.jpg", "image/jpeg", "EQ-RETRY");
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
            var callbacks = new KmDocumentDeletionV2CallbackService(factory);
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
        IKmDocumentDeletionV2Client adapter) => new(factory, new NoopCaseDeletionClient(),
        Options.Create(new KmDeletionOptions
        { Enabled = true, EquipmentEnabled = true, Scope = "mock.equipment.delete" }),
        Options.Create(new KmIntegrationOptions
        {
            BaseUrl = "https://km.mock.test/", TimeoutSeconds = 2, LeaseSeconds = 30,
            MaxRetryCount = 4
        }), adapter);

    private static KmDocumentDeletionV2Callback Completed(
        KmDocumentDeletionV2Request request, string jobId)
    {
        var now = DateTimeOffset.UtcNow;
        return new("2.0", Guid.NewGuid(), "CSIT",
            request.DeletionRequestId, jobId, request.Resource, "Completed",
            new KmDeletionTimestamps(now, now.AddSeconds(2), null),
            new KmDeletionResult(true, now.AddSeconds(1),
                new KmDeletionCleanup("Deleted", "Deleted", "Deleted", "Deleted",
                    "Deleted", "Deleted", "NotPresent")), null);
    }

    private static KmDocumentDeletionV2Callback Failed(
        KmDocumentDeletionV2Request request, string jobId)
    {
        var now = DateTimeOffset.UtcNow;
        return new("2.0", Guid.NewGuid(), "CSIT",
            request.DeletionRequestId, jobId, request.Resource, "Failed",
            new KmDeletionTimestamps(now, null, now.AddSeconds(1)), null,
            new KmDeletionError("KM_TEMPORARY", "Synthetic retryable failure.", true));
    }

    private static Task<Guid> SeedSentFileAsync(Factory factory, string root, string name,
        string equipmentId) => SeedSentFileAsync(factory, root, name, "application/pdf", equipmentId);

    private static async Task<Guid> SeedSentFileAsync(Factory factory, string root, string name,
        string contentType, string equipmentId)
    {
        var id = await SeedFileAsync(factory, root, name, contentType, true, equipmentId);
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
        : IKmDocumentDeletionV2Client
    {
        private readonly Queue<KmDeletionDeliveryResult> queued = new(results);
        private readonly Dictionary<Guid, string> jobs = [];
        public List<KmDocumentDeletionV2Request> Requests { get; } = [];
        public string JobId(Guid requestId) => jobs[requestId];
        public Task<KmDeletionDeliveryResult> SubmitAsync(KmDocumentDeletionV2Request request,
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
        private readonly DbContextOptions<AppDbContext> options;

        public Factory()
        {
            options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        }

        public Factory(string connection)
        {
            options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(connection).Options;
        }

        public AppDbContext CreateDbContext() => new(options);
    }
}
