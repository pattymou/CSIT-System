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

            var audited = await SeedSentFileAsync(factory, root, "audited.pdf",
                "application/pdf", "EQ-PG-AUDITED",
                "EBA1F6012F871D86F78B4C95E1A5803842D7BC7F0E8CF82B66D261C5285452F8");
            var sibling = await SeedSentFileAsync(factory, root, "sibling.pdf",
                "application/pdf", "EQ-PG-SIBLING");
            Guid auditedOutboxId;
            await using (var db = factory.CreateDbContext())
            {
                var outbox = await db.KmIntegrationOutbox.SingleAsync(x =>
                    x.SourceEntityId == audited);
                auditedOutboxId = outbox.Id;
                var now = DateTime.UtcNow;
                outbox.CallbackProcessing(now);
                outbox.CallbackCompleted(now);
                var analysisCallbackId = Guid.NewGuid();
                db.KmAnalysisCallbackReceipts.AddRange(
                    new KmAnalysisCallbackReceipt
                    {
                        CallbackId = Guid.NewGuid(),
                        OutboxId = outbox.Id,
                        Endpoint = "document-jobs/status",
                        Status = "Processing",
                        PayloadSha256 = new string('1', 64),
                        ReceivedAt = now.AddSeconds(-2)
                    },
                    new KmAnalysisCallbackReceipt
                    {
                        CallbackId = analysisCallbackId,
                        OutboxId = outbox.Id,
                        Endpoint = "document-jobs/status",
                        Status = "Completed",
                        PayloadSha256 = new string('2', 64),
                        ReceivedAt = now
                    });
                db.KmDocumentAnalysisResults.Add(KmDocumentAnalysisResult.Create(
                    Guid.NewGuid(), analysisCallbackId, outbox,
                    KmAnalysisSourceTypes.EquipmentAttachment, audited, audited, 1,
                    outbox.Id, outbox.KmJobId!, new string('e', 64),
                    "2.0", """{"summary":"pg-audited"}""", now, now, now, 1,
                    new string('2', 64)));
                await db.SaveChangesAsync();

                var requested = await new EquipmentFileDeletionService(db)
                    .RequestAsync(audited, "pg-test");
                Assert.False(requested!.CompletedLocally);
            }

            Assert.Equal(1, await Dispatcher(factory, adapter).ProcessBatchAsync());
            var auditedRequest = adapter.Requests.Last();
            await using (var simulateFailureDb = factory.CreateDbContext())
            {
                var job = await simulateFailureDb.EquipmentFileDeletionJobs.SingleAsync(x =>
                    x.Id == auditedRequest.DeletionRequestId);
                var lowerCallback = Completed(
                    auditedRequest with
                    {
                        Resource = auditedRequest.Resource with
                        {
                            Sha256 = auditedRequest.Resource.Sha256.ToLowerInvariant()
                        }
                    },
                    adapter.JobId(auditedRequest.DeletionRequestId));
                job.ApplyCallback(lowerCallback, DateTime.UtcNow);
                job.BeginLocalCleanup(DateTime.UtcNow);
                job.MarkCleanupFailed("LOCAL_DB_CLEANUP_FAILED",
                    "本地清理資料更新失敗，等待安全重試。", DateTime.UtcNow);
                await simulateFailureDb.SaveChangesAsync();
            }

            await using (var cleanupDb = factory.CreateDbContext())
            {
                Assert.True(await new EquipmentFileDeletionService(cleanupDb)
                    .TryCompleteLocalCleanupAsync(auditedRequest.DeletionRequestId));
            }

            await using var verifyAudited = factory.CreateDbContext();
            var completedAuditedJob = await verifyAudited.EquipmentFileDeletionJobs
                .SingleAsync(x => x.Id == auditedRequest.DeletionRequestId);
            Assert.Equal(EquipmentFileDeletionStatuses.Completed, completedAuditedJob.Status);
            Assert.Null(completedAuditedJob.LastErrorCode);
            Assert.NotNull(completedAuditedJob.DbRecordDeletedAt);
            Assert.NotNull(completedAuditedJob.PhysicalFileDeletedAt);
            Assert.NotNull(completedAuditedJob.CompletedAt);
            Assert.False(await verifyAudited.ApparatusFiles.AnyAsync(x => x.Id == audited));
            Assert.False(File.Exists(Path.Combine(root, "audited.pdf")));
            Assert.Empty(Directory.GetFiles(root, "audited.pdf.csit-delete-*"));
            var retainedOutbox = await verifyAudited.KmIntegrationOutbox
                .SingleAsync(x => x.Id == auditedOutboxId);
            Assert.Equal(KmIntegrationStatus.Completed, retainedOutbox.Status);
            Assert.Equal(2, await verifyAudited.KmAnalysisCallbackReceipts
                .CountAsync(x => x.OutboxId == auditedOutboxId));
            Assert.Single(await verifyAudited.KmDocumentAnalysisResults
                .Where(x => x.OutboxId == auditedOutboxId).ToListAsync());
            Assert.True(await verifyAudited.ApparatusFiles.AnyAsync(x => x.Id == sibling));
            Assert.True(File.Exists(Path.Combine(root, "sibling.pdf")));
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

    [Fact]
    public async Task ExactFailedHttp400JobIsAuditedRecoveredAndDispatchedWithoutChangingIdentity()
    {
        var factory = new Factory();
        var root = NewRoot();
        try
        {
            var fileId = await SeedSentFileAsync(factory, root, "manual-recovery.pdf",
                "EQ-MANUAL-RECOVERY");
            EquipmentFileDeletionJob failed;
            await using (var requestDb = factory.CreateDbContext())
            {
                await new EquipmentFileDeletionService(requestDb).RequestAsync(fileId, "writer");
                failed = await requestDb.EquipmentFileDeletionJobs.SingleAsync();
                var lease = failed.ClaimDispatch(DateTime.UtcNow, TimeSpan.FromMinutes(1));
                failed.DeliveryFailed(lease, "HTTP_400", "Old KM contract rejected equipmentId.",
                    false, DateTime.UtcNow);
                await requestDb.SaveChangesAsync();
            }
            var original = new
            {
                failed.Id,
                failed.ApparatusFileId,
                failed.DocumentId,
                failed.CanonicalIngestionEventId,
                failed.EquipmentId,
                failed.FileName
            };
            var adapter = new MockEquipmentDeletionApi();
            var result = await new EquipmentFileDeletionManualRecoveryService(
                factory, adapter,
                Options.Create(new KmDeletionOptions
                { Enabled = true, EquipmentEnabled = true, Scope = "mock.equipment.delete" }),
                Options.Create(new KmIntegrationOptions
                {
                    BaseUrl = "https://km.mock.test/", TimeoutSeconds = 2,
                    LeaseSeconds = 30, MaxRetryCount = 4
                })).RecoverAndDispatchAsync(failed.Id, "dev-admin",
                    "KM equipment identifier contract repaired.");

            Assert.True(result.KmHttpAccepted);
            Assert.Equal(EquipmentFileDeletionStatuses.Accepted, result.Status);
            Assert.Single(adapter.Requests);
            await using var verify = factory.CreateDbContext();
            var stored = await verify.EquipmentFileDeletionJobs.SingleAsync();
            Assert.Equal(original.Id, stored.Id);
            Assert.Equal(original.ApparatusFileId, stored.ApparatusFileId);
            Assert.Equal(original.DocumentId, stored.DocumentId);
            Assert.Equal(original.CanonicalIngestionEventId, stored.CanonicalIngestionEventId);
            Assert.Equal(original.EquipmentId, stored.EquipmentId);
            Assert.Equal(original.FileName, stored.FileName);
            Assert.Equal(2, stored.AttemptCount);
            var audit = await verify.ApprovalAuditEvents.SingleAsync(x =>
                x.SubmissionId == stored.Id &&
                x.Action == ApprovalAuditActions.EquipmentFileDeletionManualRecovery);
            Assert.Equal("dev-admin", audit.ActorAccount);
            Assert.Equal("KM equipment identifier contract repaired.", audit.Reason);
            Assert.Contains(stored.DocumentId.ToString(), audit.Details);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("FailedOther")]
    [InlineData("FailedAttemptTwo")]
    [InlineData("FailedRetryable")]
    [InlineData("FailedWithKmJob")]
    [InlineData("Pending")]
    [InlineData("Dispatching")]
    [InlineData("Accepted")]
    [InlineData("Completed")]
    public void ManualRecoveryRejectsEveryNonExactState(string state)
    {
        var file = NewEntityFile();
        var job = EquipmentFileDeletionJob.Create(file, "writer", state != "Completed",
            DateTime.UtcNow, state == "Completed" ? null : Guid.NewGuid());
        if (state == "FailedOther")
        {
            var lease = job.ClaimDispatch(DateTime.UtcNow, TimeSpan.FromMinutes(1));
            job.DeliveryFailed(lease, "HTTP_503", null, false, DateTime.UtcNow);
        }
        else if (state == "FailedAttemptTwo")
        {
            var lease = job.ClaimDispatch(DateTime.UtcNow, TimeSpan.FromMinutes(1));
            job.DeliveryFailed(lease, "HTTP_503", null, true, DateTime.UtcNow);
            lease = job.ClaimDispatch(DateTime.UtcNow, TimeSpan.FromMinutes(1));
            job.DeliveryFailed(lease, "HTTP_400", null, false, DateTime.UtcNow);
        }
        else if (state == "FailedRetryable")
        {
            var lease = job.ClaimDispatch(DateTime.UtcNow, TimeSpan.FromMinutes(1));
            job.DeliveryFailed(lease, "HTTP_400", null, true, DateTime.UtcNow);
        }
        else if (state == "FailedWithKmJob")
        {
            var lease = job.ClaimDispatch(DateTime.UtcNow, TimeSpan.FromMinutes(1));
            job.DeliveryAccepted(lease, "KM-JOB", DateTime.UtcNow);
            job.ApplyCallback(new KmDocumentDeletionV2Callback(
                "2.0", Guid.NewGuid(), "CSIT", job.Id, "KM-JOB", job.Resource, "Failed",
                new KmDeletionTimestamps(null, null, DateTimeOffset.UtcNow), null,
                new KmDeletionError("KM_FAILED", "Deletion failed.", false)), DateTime.UtcNow);
        }
        else if (state == "Dispatching")
            job.ClaimDispatch(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        else if (state == "Accepted")
        {
            var lease = job.ClaimDispatch(DateTime.UtcNow, TimeSpan.FromMinutes(1));
            job.DeliveryAccepted(lease, "KM-JOB", DateTime.UtcNow);
        }
        else if (state == "Completed")
        {
            job.BeginLocalCleanup(DateTime.UtcNow);
            job.MarkLocalCleanupCompleted(true, DateTime.UtcNow);
        }

        Assert.False(job.CanRecoverFailedHttp400());
        Assert.Throws<InvalidOperationException>(() =>
            job.RecoverFailedHttp400ForManualDispatch(DateTime.UtcNow));
    }

    [Fact]
    public async Task UppercaseRequestAndLowercaseCallbackSha256_AcceptsProcessingAndCompleted_WhileRejectingTrueMismatches()
    {
        const string upperSha256 = "EBA1F6012F871D86F78B4C95E1A5803842D7BC7F0E8CF82B66D261C5285452F8";
        const string lowerSha256 = "eba1f6012f871d86f78b4c95e1a5803842d7bc7f0e8cf82b66d261c5285452f8";
        const string wrongSha256 = "0ba1f6012f871d86f78b4c95e1a5803842d7bc7f0e8cf82b66d261c5285452f8";

        var factory = new Factory();
        var root = NewRoot();
        try
        {
            var fileId = await SeedSentFileAsync(factory, root, "case-sha.pdf",
                "application/pdf", "A202611160609515326246", upperSha256);
            await using (var db = factory.CreateDbContext())
                await new EquipmentFileDeletionService(db).RequestAsync(fileId, "admin");

            var adapter = new MockEquipmentDeletionApi(
                KmDeletionDeliveryResult.Success("KM-DEL-000061"));
            Assert.Equal(1, await Dispatcher(factory, adapter).ProcessBatchAsync());
            var request = Assert.Single(adapter.Requests);
            Assert.Equal(upperSha256, request.Resource.Sha256);

            var lowercaseResource = request.Resource with { Sha256 = lowerSha256 };
            var startedAt = DateTimeOffset.UtcNow;
            var processingCallback = new KmDocumentDeletionV2Callback(
                "2.0", Guid.NewGuid(), "CSIT", request.DeletionRequestId, "KM-DEL-000061",
                lowercaseResource, "Processing",
                new KmDeletionTimestamps(startedAt, null, null), null, null);
            var completedCallback = new KmDocumentDeletionV2Callback(
                "2.0", Guid.NewGuid(), "CSIT", request.DeletionRequestId, "KM-DEL-000061",
                lowercaseResource, "Completed",
                new KmDeletionTimestamps(startedAt, startedAt.AddSeconds(2), null),
                new KmDeletionResult(true, startedAt.AddSeconds(1),
                    new KmDeletionCleanup("Deleted", "Deleted", "Deleted", "Deleted",
                        "Deleted", "Deleted", "NotPresent")), null);

            var otherDocId = Guid.NewGuid();
            var mismatches = new[]
            {
                processingCallback with
                {
                    CallbackId = Guid.NewGuid(),
                    Resource = lowercaseResource with { Sha256 = wrongSha256 }
                },
                processingCallback with
                {
                    CallbackId = Guid.NewGuid(),
                    Resource = lowercaseResource with
                    {
                        SourceEntityId = otherDocId,
                        DocumentId = otherDocId
                    }
                },
                processingCallback with
                {
                    CallbackId = Guid.NewGuid(),
                    Resource = lowercaseResource with
                    {
                        CanonicalIngestionEventId = Guid.NewGuid()
                    }
                },
                processingCallback with
                {
                    CallbackId = Guid.NewGuid(),
                    KmDeletionJobId = "KM-DEL-999999"
                },
                processingCallback with
                {
                    CallbackId = Guid.NewGuid(),
                    Resource = lowercaseResource with
                    {
                        EquipmentId = "a202611160609515326246"
                    }
                }
            };

            var callbacks = new KmDocumentDeletionV2CallbackService(factory);
            foreach (var mismatch in mismatches)
            {
                Assert.True(mismatch.IsValid());
                Assert.Equal(KmDeletionCallbackOutcome.Conflict,
                    await callbacks.ApplyAsync(mismatch));
                await using var checkDb = factory.CreateDbContext();
                var untouched = await checkDb.EquipmentFileDeletionJobs.SingleAsync();
                Assert.Throws<InvalidOperationException>(() =>
                    untouched.ApplyCallback(mismatch, DateTime.UtcNow));
            }

            Assert.Equal(KmDeletionCallbackOutcome.Accepted,
                await callbacks.ApplyAsync(processingCallback));
            await using (var afterProcessing = factory.CreateDbContext())
            {
                var job = await afterProcessing.EquipmentFileDeletionJobs.SingleAsync();
                Assert.Equal(EquipmentFileDeletionStatuses.Processing, job.Status);
                Assert.Equal(upperSha256, job.Sha256);
                Assert.True(await afterProcessing.ApparatusFiles.AnyAsync(x => x.Id == fileId));
                Assert.True(File.Exists(Path.Combine(root, "case-sha.pdf")));
            }

            Assert.Equal(KmDeletionCallbackOutcome.Accepted,
                await callbacks.ApplyAsync(completedCallback));
            Assert.Equal(KmDeletionCallbackOutcome.Accepted,
                await callbacks.ApplyAsync(completedCallback));

            await using var verify = factory.CreateDbContext();
            var completedJob = await verify.EquipmentFileDeletionJobs.SingleAsync();
            Assert.Equal(EquipmentFileDeletionStatuses.Completed, completedJob.Status);
            Assert.Equal(upperSha256, completedJob.Sha256);
            Assert.NotNull(completedJob.KmVerifiedAt);
            Assert.False(await verify.ApparatusFiles.AnyAsync(x => x.Id == fileId));
            Assert.False(File.Exists(Path.Combine(root, "case-sha.pdf")));
            Assert.Equal(2, await verify.KmDocumentDeletionV2Callbacks.CountAsync(x =>
                x.DeletionRequestId == request.DeletionRequestId));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AuditedOutboxIsRetainedOnLocalCleanup_UnauditedOutboxIsRemoved_AndCleanupFailedCanRecover()
    {
        var factory = new Factory();
        var root = NewRoot();
        try
        {
            var auditedFileId = await SeedSentFileAsync(factory, root, "audited-eq.pdf",
                "application/pdf", "EQ-AUDITED");
            var unauditedFileId = await SeedSentFileAsync(factory, root, "unaudited-eq.pdf",
                "application/pdf", "EQ-UNAUDITED");
            var siblingFileId = await SeedSentFileAsync(factory, root, "sibling-eq.pdf",
                "application/pdf", "EQ-SIBLING");

            Guid auditedOutboxId;
            await using (var db = factory.CreateDbContext())
            {
                var outbox = await db.KmIntegrationOutbox.SingleAsync(x =>
                    x.SourceEntityId == auditedFileId);
                auditedOutboxId = outbox.Id;
                var now = DateTime.UtcNow;
                outbox.CallbackProcessing(now);
                outbox.CallbackCompleted(now);
                var callbackId = Guid.NewGuid();
                db.KmAnalysisCallbackReceipts.Add(new KmAnalysisCallbackReceipt
                {
                    CallbackId = callbackId,
                    OutboxId = outbox.Id,
                    Endpoint = "document-jobs/status",
                    Status = "Completed",
                    PayloadSha256 = new string('c', 64),
                    ReceivedAt = now
                });
                db.KmDocumentAnalysisResults.Add(KmDocumentAnalysisResult.Create(
                    Guid.NewGuid(), callbackId, outbox,
                    KmAnalysisSourceTypes.EquipmentAttachment, auditedFileId, auditedFileId, 1,
                    outbox.Id, outbox.KmJobId!, new string('a', 64),
                    "2.0", """{"summary":"audited"}""", now, now, now, 1,
                    new string('c', 64)));
                await db.SaveChangesAsync();

                await new EquipmentFileDeletionService(db).RequestAsync(auditedFileId, "admin");
                await new EquipmentFileDeletionService(db).RequestAsync(unauditedFileId, "admin");
            }

            var adapter = new MockEquipmentDeletionApi();
            Assert.Equal(2, await Dispatcher(factory, adapter).ProcessBatchAsync());
            var auditedRequest = adapter.Requests.Single(x => x.Resource.SourceEntityId == auditedFileId);
            var unauditedRequest = adapter.Requests.Single(x => x.Resource.SourceEntityId == unauditedFileId);

            var callbacks = new KmDocumentDeletionV2CallbackService(factory);
            Assert.Equal(KmDeletionCallbackOutcome.Accepted,
                await callbacks.ApplyAsync(Completed(unauditedRequest,
                    adapter.JobId(unauditedRequest.DeletionRequestId))));

            var completedAuditedCallback = Completed(auditedRequest,
                adapter.JobId(auditedRequest.DeletionRequestId));
            await using (var failDb = factory.CreateDbContext())
            {
                var job = await failDb.EquipmentFileDeletionJobs.SingleAsync(x =>
                    x.Id == auditedRequest.DeletionRequestId);
                job.ApplyCallback(completedAuditedCallback, DateTime.UtcNow);
                job.BeginLocalCleanup(DateTime.UtcNow);
                job.MarkCleanupFailed("LOCAL_DB_CLEANUP_FAILED",
                    "本地清理資料更新失敗，等待安全重試。", DateTime.UtcNow);
                await failDb.SaveChangesAsync();
            }

            await using (var recoverDb = factory.CreateDbContext())
            {
                var recovered = await new EquipmentFileDeletionService(recoverDb)
                    .RequestAsync(auditedFileId, "admin");
                Assert.NotNull(recovered);
                Assert.True(recovered!.CompletedLocally);
                Assert.Equal(EquipmentFileDeletionStatuses.Completed, recovered.Status);
            }

            await using var verify = factory.CreateDbContext();
            Assert.False(await verify.ApparatusFiles.AnyAsync(x =>
                x.Id == auditedFileId || x.Id == unauditedFileId));
            Assert.True(await verify.ApparatusFiles.AnyAsync(x => x.Id == siblingFileId));
            Assert.False(File.Exists(Path.Combine(root, "audited-eq.pdf")));
            Assert.False(File.Exists(Path.Combine(root, "unaudited-eq.pdf")));
            Assert.True(File.Exists(Path.Combine(root, "sibling-eq.pdf")));
            Assert.False(await verify.KmIntegrationOutbox.AnyAsync(x =>
                x.SourceEntityId == unauditedFileId));
            var retainedOutbox = await verify.KmIntegrationOutbox.SingleAsync(x =>
                x.Id == auditedOutboxId);
            Assert.Equal(KmIntegrationStatus.Completed, retainedOutbox.Status);
            Assert.Single(await verify.KmAnalysisCallbackReceipts
                .Where(x => x.OutboxId == auditedOutboxId).ToListAsync());
            Assert.Single(await verify.KmDocumentAnalysisResults
                .Where(x => x.OutboxId == auditedOutboxId).ToListAsync());
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
        string contentType, string equipmentId, string? sha256 = null)
    {
        var id = await SeedFileAsync(factory, root, name, contentType, true, equipmentId, sha256);
        await using var db = factory.CreateDbContext();
        var outbox = await db.KmIntegrationOutbox.SingleAsync(x => x.SourceEntityId == id);
        var lease = Guid.NewGuid();
        outbox.Claim(lease, DateTime.UtcNow, TimeSpan.FromMinutes(5));
        outbox.Sent(lease, "INGEST-" + id.ToString("N"), DateTime.UtcNow);
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<Guid> SeedFileAsync(Factory factory, string root, string name,
        string contentType, bool withOutbox, string equipmentId = "EQ-DELETE",
        string? sha256 = null)
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
            Sha256 = sha256 ?? new string('a', 64), ArchiveStatus = FileArchiveStatuses.Archived,
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

    private static ApparatusFile NewEntityFile()
    {
        var id = Guid.NewGuid();
        return new ApparatusFile
        {
            Id = id, ApparatusId = "EQ-ENTITY", FileName = "entity.pdf",
            OriginalFileName = "entity.pdf", FilePath = "C:\\isolated",
            ContentType = "application/pdf", FileSize = 1,
            Sha256 = new string('a', 64), ArchiveStatus = FileArchiveStatuses.Archived,
            ArchiveUpdatedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow
        };
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
