using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public sealed class EquipmentFileDeletionManualRecoveryException(
    string stage, string message) : Exception(message)
{
    public string Stage { get; } = stage;
}

public sealed record EquipmentFileDeletionManualRecoveryResult(
    Guid JobId, Guid DeletionRequestId, Guid DocumentId,
    Guid CanonicalIngestionEventId, string Status, string? KmDeletionJobId,
    bool KmHttpAccepted, string? ErrorCode);

public sealed class EquipmentFileDeletionManualRecoveryService(
    IDbContextFactory<AppDbContext> dbFactory,
    IKmDocumentDeletionV2Client client,
    IOptions<KmDeletionOptions> deletion,
    IOptions<KmIntegrationOptions> integration)
{
    public async Task<EquipmentFileDeletionManualRecoveryResult> RecoverAndDispatchAsync(
        Guid jobId, string requestedBy, string reason,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(jobId, requestedBy, reason);
        ValidateConfiguration();
        var actor = requestedBy.Trim();
        var recoveryReason = reason.Trim();
        Guid lease;
        KmDocumentDeletionV2Request request;

        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted,
                    cancellationToken) : null;
            var job = db.Database.ProviderName?.Contains("Npgsql",
                    StringComparison.OrdinalIgnoreCase) == true
                ? await db.EquipmentFileDeletionJobs.FromSqlInterpolated($"""
                    SELECT * FROM equipment_file_deletion_jobs
                    WHERE id = {jobId} FOR UPDATE
                    """).SingleOrDefaultAsync(cancellationToken)
                : await db.EquipmentFileDeletionJobs.SingleOrDefaultAsync(
                    x => x.Id == jobId, cancellationToken);
            if (job is null)
                throw new EquipmentFileDeletionManualRecoveryException(
                    "JobLookup", "The specified equipment deletion job does not exist.");
            if (!job.CanRecoverFailedHttp400() ||
                job.AttemptCount >= integration.Value.MaxRetryCount)
                throw new EquipmentFileDeletionManualRecoveryException(
                    "StateGate", "The specified job no longer meets the exact recovery gate.");
            if (await db.EquipmentFileDeletionCallbacks.AsNoTracking()
                    .AnyAsync(x => x.DeletionJobId == jobId, cancellationToken) ||
                await db.KmDocumentDeletionV2Callbacks.AsNoTracking().AnyAsync(x =>
                    x.DeletionRequestId == jobId &&
                    x.SourceType == KmDocumentDeletionV2SourceTypes.EquipmentAttachment,
                    cancellationToken))
                throw new EquipmentFileDeletionManualRecoveryException(
                    "CallbackGate", "The specified job already has callback evidence.");

            var fileExists = await db.ApparatusFiles.AsNoTracking().AnyAsync(x =>
                x.Id == job.ApparatusFileId && x.Id == job.DocumentId &&
                x.ApparatusId == job.EquipmentId, cancellationToken);
            if (!fileExists || job.CanonicalIngestionEventId is not { } canonicalEventId ||
                canonicalEventId == Guid.Empty || !job.ToRequest().IsValid())
                throw new EquipmentFileDeletionManualRecoveryException(
                    "ResourceGate", "The specified job no longer has its exact source resource.");

            var ingestion = db.Database.ProviderName?.Contains("Npgsql",
                    StringComparison.OrdinalIgnoreCase) == true
                ? await db.KmIntegrationOutbox.FromSqlInterpolated($"""
                    SELECT * FROM km_integration_outbox
                    WHERE event_type IN ('EquipmentDocumentArchived', 'EquipmentAttachmentArchived')
                      AND source_entity_type = 'ApparatusFile'
                      AND source_entity_id = {job.ApparatusFileId}
                      AND event_id = {canonicalEventId}
                    FOR UPDATE
                    """).SingleOrDefaultAsync(cancellationToken)
                : await db.KmIntegrationOutbox.SingleOrDefaultAsync(x =>
                    (x.EventType == KmIntegrationEventTypes.EquipmentDocumentArchived ||
                     x.EventType == KmIntegrationEventTypes.EquipmentAttachmentArchived) &&
                    x.SourceEntityType == KmIntegrationSourceEntityTypes.ApparatusFile &&
                    x.SourceEntityId == job.ApparatusFileId &&
                    x.EventId == canonicalEventId, cancellationToken);
            if (ingestion is null || ingestion.EventId != canonicalEventId ||
                ingestion.DocumentId != job.DocumentId || ingestion.Version != job.Version ||
                ingestion.Status == KmIntegrationStatus.Dispatching)
                throw new EquipmentFileDeletionManualRecoveryException(
                    "IngestionGate", "The correlated ingestion event is missing, changed, or leased.");

            var now = DateTime.UtcNow;
            var details = JsonSerializer.Serialize(new
            {
                JobId = job.Id,
                DeletionRequestId = job.Id,
                job.ApparatusFileId,
                job.DocumentId,
                CanonicalIngestionEventId = canonicalEventId,
                job.EquipmentId,
                job.FileName,
                PreviousStatus = job.Status,
                job.LastErrorCode,
                job.LastErrorRetryable,
                job.AttemptCount
            });
            db.ApprovalAuditEvents.Add(ApprovalAuditEvent.Create(
                job.Id, "equipment", "equipment",
                ApprovalAuditActions.EquipmentFileDeletionManualRecovery,
                actor, now, null, null, recoveryReason, job.AttemptCount, details));
            job.RecoverFailedHttp400ForManualDispatch(now);
            lease = job.ClaimDispatch(now,
                TimeSpan.FromSeconds(integration.Value.LeaseSeconds));
            request = job.ToRequest();
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        }

        var delivery = await client.SubmitAsync(request, cancellationToken);
        await using var resultDb = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var resultTransaction = resultDb.Database.IsRelational()
            ? await resultDb.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted,
                cancellationToken) : null;
        var current = resultDb.Database.ProviderName?.Contains("Npgsql",
                StringComparison.OrdinalIgnoreCase) == true
            ? await resultDb.EquipmentFileDeletionJobs.FromSqlInterpolated($"""
                SELECT * FROM equipment_file_deletion_jobs
                WHERE id = {jobId} FOR UPDATE
                """).SingleAsync(cancellationToken)
            : await resultDb.EquipmentFileDeletionJobs.SingleAsync(
                x => x.Id == jobId, cancellationToken);
        if (current.Status != EquipmentFileDeletionStatuses.Dispatching ||
            current.LeaseToken != lease)
            throw new EquipmentFileDeletionManualRecoveryException(
                "DeliveryCommit", "The recovery lease changed before delivery was persisted.");
        if (delivery.Accepted)
            current.DeliveryAccepted(lease, delivery.JobId!, DateTime.UtcNow);
        else
            current.DeliveryFailed(lease, delivery.ErrorCode ?? "UNKNOWN", null,
                delivery.Retryable, DateTime.UtcNow);
        await resultDb.SaveChangesAsync(cancellationToken);
        if (resultTransaction is not null)
            await resultTransaction.CommitAsync(cancellationToken);

        return new(current.Id, current.Id, current.DocumentId,
            current.CanonicalIngestionEventId!.Value, current.Status,
            current.KmDeletionJobId, delivery.Accepted, current.LastErrorCode);
    }

    private void ValidateConfiguration()
    {
        if (!deletion.Value.Enabled || !deletion.Value.EquipmentEnabled ||
            string.IsNullOrWhiteSpace(deletion.Value.Scope) ||
            integration.Value.LeaseSeconds <= integration.Value.TimeoutSeconds + 5 ||
            integration.Value.MaxRetryCount < 2 || integration.Value.TimeoutSeconds < 1 ||
            !Uri.TryCreate(integration.Value.BaseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttps || baseUri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment))
            throw new EquipmentFileDeletionManualRecoveryException(
                "Configuration", "Equipment deletion recovery configuration is invalid.");
    }

    private static void ValidateRequest(Guid jobId, string requestedBy, string reason)
    {
        if (jobId == Guid.Empty)
            throw new ArgumentException("Job ID is required.", nameof(jobId));
        if (string.IsNullOrWhiteSpace(requestedBy) || requestedBy.Trim().Length > 100 ||
            requestedBy.Any(char.IsControl))
            throw new ArgumentException("A safe recovery actor is required.", nameof(requestedBy));
        var trimmed = reason?.Trim() ?? string.Empty;
        if (trimmed.Length is < 5 or > 500 || trimmed.Any(ch => char.IsControl(ch) ||
            char.GetUnicodeCategory(ch) is UnicodeCategory.Format or
                UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) ||
            new[] { "authorization:", "bearer ", "password", "client_secret", "access_token" }
                .Any(x => trimmed.Contains(x, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("A safe recovery reason of 5 to 500 characters is required.",
                nameof(reason));
    }
}
