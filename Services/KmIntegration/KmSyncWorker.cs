using System.Data;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Config;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public sealed class KmSyncWorker(
    IDbContextFactory<AppDbContext> dbFactory,
    IKmIntegrationClient client,
    IOptions<KmIntegrationOptions> configured,
    ILogger<KmSyncWorker> logger,
    IOptions<DocumentStorageOptions>? documentStorage = null)
{
    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken = default)
    {
        var options = configured.Value;
        if (!options.Enabled) return 0;
        ValidateOptions(options);
        await EnsurePendingDocumentLibraryOutboxAsync(cancellationToken);
        var count = 0;
        for (var i = 0; i < 20; i++)
        {
            var claim = await ClaimNextAsync(options, cancellationToken);
            if (claim is null) break;
            await DispatchAsync(claim.Value.Id, claim.Value.Lease, options, cancellationToken);
            count++;
        }
        return count;
    }

    private async Task<(Guid Id, Guid Lease)?> ClaimNextAsync(KmIntegrationOptions options,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken) : null;
        var now = DateTime.UtcNow;
        KmIntegrationOutbox? row;
        if (db.Database.IsRelational())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE km_integration_outbox SET status = 'Failed', lease_token = NULL,
                    lease_expires_at = NULL, next_retry_at = NULL,
                    last_error_code = 'RETRY_EXHAUSTED',
                    last_error_message = 'KM delivery retry limit reached.', updated_at = {now}
                WHERE status = 'Dispatching' AND lease_expires_at <= {now}
                    AND retry_count >= {options.MaxRetryCount}
                """, cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE document_processing_jobs AS job
                SET status = 'Failed', km_failed_at = COALESCE(job.km_failed_at, {now}),
                    km_error_code = 'RETRY_EXHAUSTED',
                    km_error_message = 'KM delivery retry limit reached.',
                    km_error_retryable = FALSE,
                    last_error = 'RETRY_EXHAUSTED: KM delivery retry limit reached.',
                    updated_at = {now}
                FROM km_integration_outbox AS outbox
                WHERE outbox.event_id = job.id AND outbox.source_entity_id = job.document_file_id
                  AND outbox.event_type = 'DocumentLibraryFileUploaded'
                  AND outbox.source_entity_type = 'DocumentLibraryFile'
                  AND outbox.status = 'Failed' AND outbox.last_error_code = 'RETRY_EXHAUSTED'
                  AND job.status IN ('Pending', 'Accepted')
                """, cancellationToken);
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.Transaction = transaction!.GetDbTransaction();
            command.CommandText = """
                SELECT id FROM km_integration_outbox
                WHERE ((event_type = 'TestReportCommitted' AND source_entity_type = 'TestReport' AND
                  NOT EXISTS (SELECT 1 FROM verification_case_deletion_jobs AS deletion
                    WHERE deletion.record_id = km_integration_outbox.project_id
                    AND deletion.status IN ('WaitingForKm', 'KmVerified', 'LocalReady', 'LocalCleanup', 'Completed')))
                  OR (event_type = 'DocumentLibraryFileUploaded' AND source_entity_type = 'DocumentLibraryFile'
                    AND NOT EXISTS (SELECT 1 FROM document_file_deletion_jobs AS document_deletion
                      WHERE document_deletion.document_file_id = km_integration_outbox.source_entity_id)
                    AND EXISTS (SELECT 1 FROM document_processing_jobs AS document_job
                      JOIN document_files AS document_file ON document_file.id = document_job.document_file_id
                      WHERE document_job.id = km_integration_outbox.event_id
                        AND document_file.id = km_integration_outbox.source_entity_id
                        AND document_file.document_id = km_integration_outbox.document_id
                        AND document_file.version = km_integration_outbox.version
                        AND document_file.storage_area = 'Library'
                        AND document_job.archive_status = 'Completed'
                        AND (EXISTS (SELECT 1 FROM pending_submissions AS submission
                              WHERE submission.id = document_file.id
                                AND submission.source_module_code = 'document-library'
                                AND submission.status = 'Committed'
                                AND submission.reviewer_account IS NOT NULL
                                AND submission.reviewed_at IS NOT NULL)
                          OR EXISTS (SELECT 1 FROM approval_audit_events AS audit
                              WHERE audit.submission_id = document_file.id
                                AND audit.source_module_code = 'document-library'
                                AND audit.action = 'ApprovalBypassed'))))
                  OR (event_type = 'VerificationApplicationAttachmentArchived'
                    AND source_entity_type = 'VerificationApplicationFile'
                    AND NOT EXISTS (SELECT 1 FROM document_file_deletion_jobs AS attachment_deletion
                      WHERE attachment_deletion.document_file_id = km_integration_outbox.document_id)
                    AND NOT EXISTS (SELECT 1 FROM verification_case_deletion_jobs AS case_deletion
                      JOIN verification_application_files AS attachment
                        ON attachment.id = km_integration_outbox.source_entity_id
                      JOIN verification_applications AS application
                        ON application.id = attachment.verification_application_id
                      WHERE case_deletion.record_id = application.module_record_id
                        AND case_deletion.status IN ('WaitingForKm', 'KmVerified', 'LocalReady', 'LocalCleanup', 'Completed')))
                  OR (@vendor_enabled AND event_type = 'VendorFileArchived' AND source_entity_type = 'VendorFile'
                    AND NOT EXISTS (SELECT 1 FROM vendor_file_deletion_jobs AS vendor_deletion
                      WHERE vendor_deletion.vendor_file_id = km_integration_outbox.source_entity_id))
                  OR (@equipment_enabled AND event_type IN ('EquipmentDocumentArchived', 'EquipmentAttachmentArchived')
                    AND source_entity_type = 'ApparatusFile'
                    AND NOT EXISTS (SELECT 1 FROM equipment_file_deletion_jobs AS equipment_deletion
                      WHERE equipment_deletion.apparatus_file_id = km_integration_outbox.source_entity_id))
                  OR event_type NOT IN ('TestReportCommitted', 'DocumentLibraryFileUploaded',
                       'VerificationApplicationAttachmentArchived', 'VendorFileArchived',
                       'EquipmentDocumentArchived', 'EquipmentAttachmentArchived')) AND
                  (status = 'Pending' OR retry_count < @max) AND
                  (status = 'Pending' OR
                   (status = 'Failed' AND next_retry_at <= @now) OR
                   (status = 'Dispatching' AND lease_expires_at <= @now))
                ORDER BY created_at, id LIMIT 1 FOR UPDATE SKIP LOCKED
                """;
            var max = command.CreateParameter(); max.ParameterName = "max"; max.Value = options.MaxRetryCount;
            var at = command.CreateParameter(); at.ParameterName = "now"; at.Value = now;
            var vendorEnabled = command.CreateParameter(); vendorEnabled.ParameterName = "vendor_enabled";
            vendorEnabled.Value = options.VendorEnabled;
            var equipmentEnabled = command.CreateParameter(); equipmentEnabled.ParameterName = "equipment_enabled";
            equipmentEnabled.Value = options.EquipmentEnabled;
            command.Parameters.Add(max); command.Parameters.Add(at); command.Parameters.Add(vendorEnabled);
            command.Parameters.Add(equipmentEnabled);
            var id = await command.ExecuteScalarAsync(cancellationToken);
            row = id is Guid value
                ? await db.KmIntegrationOutbox.SingleAsync(x => x.Id == value, cancellationToken) : null;
        }
        else
        {
            var exhausted = await db.KmIntegrationOutbox
                .Where(x => x.Status == KmIntegrationStatus.Dispatching && x.LeaseExpiresAt <= now &&
                    x.RetryCount >= options.MaxRetryCount)
                .ToListAsync(cancellationToken);
            foreach (var expired in exhausted)
            {
                expired.DeliveryFailed(expired.LeaseToken!.Value, "RETRY_EXHAUSTED",
                    "KM delivery retry limit reached.", null, now);
                if (expired.EventType == KmIntegrationEventTypes.DocumentLibraryFileUploaded &&
                    expired.SourceEntityType == KmIntegrationSourceEntityTypes.DocumentLibraryFile)
                {
                    var job = await db.DocumentProcessingJobs.SingleAsync(x => x.Id == expired.EventId &&
                        x.DocumentFileId == expired.SourceEntityId, cancellationToken);
                    if (job.Status is DocumentProcessingStatuses.Pending or DocumentProcessingStatuses.Accepted)
                        job.DeliveryExhausted(now, "RETRY_EXHAUSTED", "KM delivery retry limit reached.");
                }
            }
            if (exhausted.Count > 0) await db.SaveChangesAsync(cancellationToken);
            row = await db.KmIntegrationOutbox.OrderBy(x => x.CreatedAt)
                .FirstOrDefaultAsync(x =>
                    ((x.EventType == KmIntegrationEventTypes.TestReportCommitted &&
                    x.SourceEntityType == KmIntegrationSourceEntityTypes.TestReport &&
                    !db.VerificationCaseDeletionJobs.Any(deletion =>
                        deletion.RecordId == x.ProjectId &&
                        (deletion.Status == "WaitingForKm" || deletion.Status == "KmVerified" ||
                         deletion.Status == "LocalReady" || deletion.Status == "LocalCleanup" ||
                         deletion.Status == "Completed"))) ||
                     (x.EventType == KmIntegrationEventTypes.DocumentLibraryFileUploaded &&
                       x.SourceEntityType == KmIntegrationSourceEntityTypes.DocumentLibraryFile &&
                        !db.DocumentLibraryFileDeletionJobs.Any(deletion =>
                            deletion.DocumentFileId == x.SourceEntityId) &&
                       db.DocumentProcessingJobs.Any(job => job.Id == x.EventId &&
                           job.DocumentFileId == x.SourceEntityId &&
                           (job.DocumentFile.DocumentId == Guid.Empty
                               ? job.DocumentFile.Id : job.DocumentFile.DocumentId) == x.DocumentId &&
                           (job.DocumentFile.Version < 1 ? 1 : job.DocumentFile.Version) == x.Version &&
                           job.ArchiveStatus == DocumentArchiveStatuses.Completed &&
                           job.DocumentFile.StorageArea == DocumentStorageAreas.Library &&
                            (db.PendingSubmissions.Any(submission => submission.Id == x.SourceEntityId &&
                                submission.SourceModuleCode == "document-library" &&
                                submission.Status == ApprovalStatus.Committed &&
                                submission.ReviewerAccount != null && submission.ReviewedAt != null) ||
                             db.ApprovalAuditEvents.Any(audit => audit.SubmissionId == x.SourceEntityId &&
                                audit.SourceModuleCode == "document-library" &&
                                audit.Action == ApprovalAuditActions.ApprovalBypassed)))) ||
                     (x.EventType == KmIntegrationEventTypes.VerificationApplicationAttachmentArchived &&
                       x.SourceEntityType == KmIntegrationSourceEntityTypes.VerificationApplicationFile &&
                       !db.DocumentLibraryFileDeletionJobs.Any(deletion =>
                           deletion.DocumentFileId == x.DocumentId) &&
                       !db.VerificationCaseDeletionJobs.Any(deletion =>
                           db.VerificationApplicationFiles.Any(attachment =>
                               attachment.Id == x.SourceEntityId &&
                               attachment.VerificationApplication.ModuleRecordId == deletion.RecordId) &&
                           (deletion.Status == "WaitingForKm" || deletion.Status == "KmVerified" ||
                            deletion.Status == "LocalReady" || deletion.Status == "LocalCleanup" ||
                            deletion.Status == "Completed"))) ||
                     (options.VendorEnabled &&
                      x.EventType == KmIntegrationEventTypes.VendorFileArchived &&
                       x.SourceEntityType == KmIntegrationSourceEntityTypes.VendorFile &&
                       !db.VendorFileDeletionJobs.Any(deletion =>
                           deletion.VendorFileId == x.SourceEntityId)) ||
                      (options.EquipmentEnabled &&
                       (x.EventType == KmIntegrationEventTypes.EquipmentDocumentArchived ||
                        x.EventType == KmIntegrationEventTypes.EquipmentAttachmentArchived) &&
                       x.SourceEntityType == KmIntegrationSourceEntityTypes.ApparatusFile &&
                       !db.EquipmentFileDeletionJobs.Any(deletion =>
                           deletion.ApparatusFileId == x.SourceEntityId)) ||
                      (x.EventType != KmIntegrationEventTypes.TestReportCommitted &&
                       x.EventType != KmIntegrationEventTypes.DocumentLibraryFileUploaded &&
                       x.EventType != KmIntegrationEventTypes.VerificationApplicationAttachmentArchived &&
                       x.EventType != KmIntegrationEventTypes.VendorFileArchived &&
                       x.EventType != KmIntegrationEventTypes.EquipmentDocumentArchived &&
                       x.EventType != KmIntegrationEventTypes.EquipmentAttachmentArchived)) &&
                    (x.Status == KmIntegrationStatus.Pending ||
                    x.RetryCount < options.MaxRetryCount) &&
                    (x.Status == KmIntegrationStatus.Pending ||
                     (x.Status == KmIntegrationStatus.Failed && x.NextRetryAt <= now) ||
                     (x.Status == KmIntegrationStatus.Dispatching && x.LeaseExpiresAt <= now)), cancellationToken);
        }
        if (row is null) return null;
        var lease = Guid.NewGuid();
        row.Claim(lease, now, TimeSpan.FromSeconds(options.LeaseSeconds));
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("KM outbox claimed EventId={EventId} DocumentId={DocumentId} Version={Version} Attempt={Attempt} Status={Status}",
            row.EventId, row.DocumentId, row.Version, row.RetryCount + 1, row.Status);
        return (row.Id, lease);
    }

    private async Task DispatchAsync(Guid id, Guid lease, KmIntegrationOptions options,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.LeaseSeconds - 5));
        KmDeliveryResult result;
        try
        {
            await using (var db = await dbFactory.CreateDbContextAsync(deadline.Token))
            {
                var row = await db.KmIntegrationOutbox.AsNoTracking().SingleAsync(x => x.Id == id, deadline.Token);
                if (row.EventType == KmIntegrationEventTypes.DocumentLibraryFileUploaded &&
                    row.SourceEntityType == KmIntegrationSourceEntityTypes.DocumentLibraryFile)
                {
                    if (row.Version > 1)
                    {
                        var request = await BuildDocumentLibraryV2RequestAsync(
                            db, row, options, deadline.Token);
                        result = request is null
                            ? KmDeliveryResult.Failure(false, "DOCUMENT_UNAVAILABLE",
                                "Formally archived document library version is unavailable.")
                            : await client.SubmitDocumentJobAsync(request, deadline.Token);
                    }
                    else
                    {
                        var request = await BuildDocumentLibraryRequestAsync(
                            db, row, options, deadline.Token);
                        result = request is null
                            ? KmDeliveryResult.Failure(false, "DOCUMENT_UNAVAILABLE",
                                "Formally archived document library file or classification is unavailable.")
                            : await client.SubmitDocumentLibraryAsync(request, deadline.Token);
                    }
                }
                else if (row.EventType == KmIntegrationEventTypes.VendorFileArchived &&
                    row.SourceEntityType == KmIntegrationSourceEntityTypes.VendorFile)
                {
                    var request = await BuildVendorRequestAsync(db, row, options, deadline.Token);
                    result = request is null
                        ? KmDeliveryResult.Failure(false, "VENDOR_FILE_UNAVAILABLE",
                            "Approved formal Vendor file is unavailable.")
                        : await client.SubmitDocumentJobAsync(request, deadline.Token);
                }
                else if ((row.EventType == KmIntegrationEventTypes.EquipmentDocumentArchived ||
                          row.EventType == KmIntegrationEventTypes.EquipmentAttachmentArchived) &&
                    row.SourceEntityType == KmIntegrationSourceEntityTypes.ApparatusFile)
                {
                    var request = await BuildEquipmentRequestAsync(db, row, options, deadline.Token);
                    result = request is null
                        ? KmDeliveryResult.Failure(false, "EQUIPMENT_ATTACHMENT_UNAVAILABLE",
                            "Approved formal Equipment attachment is unavailable.")
                        : await client.SubmitDocumentJobAsync(request, deadline.Token);
                }
                else if (row.EventType == KmIntegrationEventTypes.VerificationApplicationAttachmentArchived &&
                    row.SourceEntityType == KmIntegrationSourceEntityTypes.VerificationApplicationFile)
                {
                    var request = await BuildVerificationAttachmentRequestAsync(
                        db, row, options, deadline.Token);
                    result = request is null
                        ? KmDeliveryResult.Failure(false, "VERIFICATION_ATTACHMENT_UNAVAILABLE",
                            "Accepted and formally archived verification attachment is unavailable.")
                        : await client.SubmitDocumentJobAsync(request, deadline.Token);
                }
                else if (row.EventType == KmIntegrationEventTypes.TestReportCommitted &&
                    row.SourceEntityType == KmIntegrationSourceEntityTypes.TestReport)
                {
                    if (row.Version > 1)
                    {
                        var request = await BuildTestReportV2RequestAsync(
                            db, row, options, deadline.Token);
                        result = request is null
                            ? KmDeliveryResult.Failure(false, "DOCUMENT_NOT_COMMITTED",
                                "Formal test report version is unavailable.")
                            : await client.SubmitDocumentJobAsync(request, deadline.Token);
                    }
                    else
                    {
                        var request = await BuildRequestAsync(db, row, deadline.Token);
                        result = request is null
                            ? KmDeliveryResult.Failure(false, "DOCUMENT_NOT_COMMITTED",
                                "Formal test report is unavailable.")
                            : await client.SubmitAsync(request, deadline.Token);
                    }
                }
                else
                {
                    result = KmDeliveryResult.Failure(false, "UNSUPPORTED_EVENT",
                        "The KM outbox event type is not supported by this worker.");
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result = KmDeliveryResult.Failure(true, "LEASE_TIMEOUT", "KM delivery exceeded its lease deadline.");
        }

        await using var update = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = update.Database.IsRelational()
            ? await update.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken) : null;
        var current = update.Database.IsRelational()
            ? await update.KmIntegrationOutbox
                .FromSqlInterpolated($"SELECT * FROM km_integration_outbox WHERE id = {id} FOR UPDATE")
                .SingleAsync(cancellationToken)
            : await update.KmIntegrationOutbox.SingleAsync(x => x.Id == id, cancellationToken);
        if (current.LeaseToken != lease || current.Status != KmIntegrationStatus.Dispatching) return;
        var now = DateTime.UtcNow;
        if (result.Accepted)
            current.Sent(lease, result.JobId!, now);
        else
        {
            var schedule = options.RetryScheduleSeconds;
            var delay = schedule[Math.Min(current.RetryCount, schedule.Length - 1)];
            var next = result.Retryable && current.RetryCount < options.MaxRetryCount
                ? now.AddSeconds(delay) : (DateTime?)null;
            current.DeliveryFailed(lease, result.ErrorCode ?? "UNKNOWN",
                result.ErrorMessage ?? "KM delivery failed.", next, now);
        }
        if (current.EventType == KmIntegrationEventTypes.DocumentLibraryFileUploaded &&
            current.SourceEntityType == KmIntegrationSourceEntityTypes.DocumentLibraryFile)
        {
            var job = await update.DocumentProcessingJobs.SingleAsync(x => x.Id == current.EventId &&
                x.DocumentFileId == current.SourceEntityId, cancellationToken);
            job.DeliveryAttempted(current.RetryCount + 1, now,
                result.Accepted ? null : result.ErrorMessage ?? "KM delivery failed.");
            if (result.Accepted)
                job.DeliveryAccepted(now);
            else if (!current.NextRetryAt.HasValue)
                job.DeliveryExhausted(now, result.ErrorCode ?? "UNKNOWN",
                    result.ErrorMessage ?? "KM delivery failed.");
        }
        await update.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("KM outbox transition EventId={EventId} DocumentId={DocumentId} Version={Version} KmJobId={KmJobId} Attempt={Attempt} Status={Status} ErrorCode={ErrorCode}",
            current.EventId, current.DocumentId, current.Version, current.KmJobId,
            current.RetryCount + 1, current.Status, current.LastErrorCode);
    }

    private static async Task<KmIngestionRequest?> BuildRequestAsync(AppDbContext db,
        KmIntegrationOutbox row, CancellationToken cancellationToken)
    {
        if (row.EventType != KmIntegrationEventTypes.TestReportCommitted ||
            row.SourceEntityType != KmIntegrationSourceEntityTypes.TestReport ||
            !row.ProjectId.HasValue)
            return null;
        if (await VerificationCaseDeletionHold.IsActiveAsync(db, row.ProjectId.Value,
            cancellationToken)) return null;
        var file = await db.ModuleCaseFiles.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == row.SourceEntityId && x.DocumentId == row.DocumentId && x.VersionNo == row.Version &&
            x.FileKind == ModuleCaseFileKinds.TestReport &&
            x.ReviewStatus == TestReportReviewStatuses.Approved && x.IsRawDataExported,
            cancellationToken);
        if (file is null || await db.PendingSubmissions.AsNoTracking().AnyAsync(x =>
            x.Id == file.Id && x.Status != ApprovalStatus.Committed, cancellationToken)) return null;
        var project = await db.ModuleRecords.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == row.ProjectId.Value, cancellationToken);
        var caseEntity = row.CaseId.HasValue
            ? await db.ModuleRecordCases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.CaseId.Value, cancellationToken)
            : null;
        var task = row.TaskId.HasValue
            ? await db.ModuleRecordTasks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.TaskId.Value, cancellationToken)
            : row.CaseId.HasValue && !string.IsNullOrWhiteSpace(file.TaskNo)
                ? await db.ModuleRecordTasks.AsNoTracking().FirstOrDefaultAsync(x =>
                    x.CaseId == row.CaseId.Value && x.TaskNo == file.TaskNo, cancellationToken)
                : null;
        if (project is null || caseEntity is null || task is null || task.CaseId != caseEntity.Id ||
            caseEntity.RecordId != project.Id) return null;
        return new KmIngestionRequest(row.EventId, row.EventType, row.SourceSystem,
            row.DocumentId, row.Version,
            new KmReference(project.Id, project.RecordNo, project.Name),
            new KmReference(caseEntity.Id, caseEntity.CaseNo, caseEntity.Name),
            new KmReference(task.Id, task.TaskNo, task.Name),
            new KmDocumentMetadata(row.FileName, row.ContentType, row.SizeBytes), row.CommittedAt);
    }

    private static async Task<DocumentLibraryIngestionRequest?> BuildDocumentLibraryRequestAsync(
        AppDbContext db,
        KmIntegrationOutbox row,
        KmIntegrationOptions options,
        CancellationToken cancellationToken)
    {
        if (row.EventId == Guid.Empty || row.DocumentId == Guid.Empty || row.Version != 1 ||
            !Uri.TryCreate(options.ServiceBaseUrl, UriKind.Absolute, out var serviceBase) ||
            serviceBase.Scheme != Uri.UriSchemeHttps || serviceBase.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(serviceBase.Query) || !string.IsNullOrEmpty(serviceBase.Fragment))
            return null;
        var job = await db.DocumentProcessingJobs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == row.EventId && x.DocumentFileId == row.SourceEntityId,
                cancellationToken);
        var file = await db.DocumentLibraryFiles.AsNoTracking()
            .Include(x => x.Folder).ThenInclude(x => x.Parent).ThenInclude(x => x!.Parent)
            .SingleOrDefaultAsync(x => x.Id == row.SourceEntityId, cancellationToken);
        if (job is null || file is null || file.StorageArea != DocumentStorageAreas.Library ||
            (file.DocumentId == Guid.Empty ? file.Id : file.DocumentId) != row.DocumentId ||
            (file.Version < 1 ? 1 : file.Version) != row.Version ||
            await db.DocumentLibraryFileDeletionJobs.AsNoTracking().AnyAsync(
                x => x.DocumentFileId == row.SourceEntityId, cancellationToken) ||
            job.ArchiveStatus != DocumentArchiveStatuses.Completed || job.ArchivedAt is null ||
            file.FileSize != row.SizeBytes || file.Sha256.Length != 64 ||
            !file.Sha256.All(Uri.IsHexDigit) || file.Sha256.Any(char.IsUpper) ||
            file.Folder.Level != 3 || file.Folder.Parent?.Level != 2 ||
            file.Folder.Parent.Parent?.Level != 1)
            return null;
        var hasApprovalEvidence = await db.PendingSubmissions.AsNoTracking().AnyAsync(x =>
                x.Id == file.Id && x.SourceModuleCode == "document-library" &&
                x.Status == ApprovalStatus.Committed && x.ReviewerAccount != null && x.ReviewedAt != null,
                cancellationToken)
            || await db.ApprovalAuditEvents.AsNoTracking().AnyAsync(x =>
                x.SubmissionId == file.Id && x.SourceModuleCode == "document-library" &&
                x.Action == ApprovalAuditActions.ApprovalBypassed, cancellationToken);
        if (!hasApprovalEvidence) return null;
        var l3 = file.Folder;
        var l2 = l3.Parent!;
        var l1 = l2.Parent!;
        var downloadPath = $"api/integrations/km/v1/document-library/files/{file.Id:D}/content";
        return new DocumentLibraryIngestionRequest(
            "1.0", "CSIT", KmIntegrationEventTypes.DocumentLibraryFileUploaded,
            file.Id, job.Id, job.CreatedAt,
            new DocumentLibraryKmFile(file.OriginalFileName, file.StandardFileName,
                file.ContentType, file.FileSize, file.Sha256,
                new Uri(serviceBase, downloadPath).AbsoluteUri),
            new DocumentLibraryClassification(
                new DocumentLibraryClassificationLevel(l1.Id, l1.Name),
                new DocumentLibraryClassificationLevel(l2.Id, l2.Name),
                new DocumentLibraryClassificationLevel(l3.Id, l3.Name)));
    }

    private async Task<KmV2DocumentJobRequest?> BuildDocumentLibraryV2RequestAsync(
        AppDbContext db,
        KmIntegrationOutbox row,
        KmIntegrationOptions options,
        CancellationToken cancellationToken)
    {
        if (row.Version <= 1 || !TryGetServiceBaseUri(options, out var serviceBase)) return null;
        var file = await db.DocumentLibraryFiles.AsNoTracking()
            .Include(x => x.ProcessingJob)
            .Include(x => x.Folder).ThenInclude(x => x.Parent).ThenInclude(x => x!.Parent)
            .SingleOrDefaultAsync(x => x.Id == row.SourceEntityId, cancellationToken);
        if (file?.ProcessingJob is null || file.DocumentId != row.DocumentId ||
            file.Version != row.Version || file.StorageArea != DocumentStorageAreas.Library ||
            file.ProcessingJob.Id != row.EventId ||
            file.ProcessingJob.ArchiveStatus != DocumentArchiveStatuses.Completed ||
            file.ProcessingJob.ArchivedAt is null || file.Folder.Level != 3 ||
            file.Folder.Parent?.Level != 2 || file.Folder.Parent.Parent?.Level != 1 ||
            !ValidSha256(file.Sha256) || file.FileSize != row.SizeBytes ||
            !file.SupersedesFileId.HasValue ||
            await DocumentLibraryFileDeletionHold.ExistsAsync(db, file.Id, cancellationToken))
            return null;
        var prior = await db.DocumentLibraryFiles.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == file.SupersedesFileId.Value, cancellationToken);
        if (prior is null || prior.DocumentId != file.DocumentId ||
            prior.Version >= file.Version || !ValidSha256(prior.Sha256)) return null;
        var approval = await FindApprovalEvidenceAsync(db, file.Id, "document-library",
            cancellationToken);
        if (approval is null || !TryResolveLibraryFile(file, out var fullPath) ||
            !await FileMatchesAsync(fullPath, file.FileSize, file.Sha256, cancellationToken))
            return null;
        var l3 = file.Folder;
        var l2 = l3.Parent!;
        var l1 = l2.Parent!;
        return CreateV2Request(row,
            new KmV2DocumentJobSource(KmAnalysisSourceTypes.DocumentLibraryFile, file.Id,
                "document-library", null, null, null, null),
            file.OriginalFileName, file.ContentType, file.FileSize, file.Sha256,
            new Uri(serviceBase!,
                $"api/integrations/km/v1/document-library/files/{file.Id:D}/content").AbsoluteUri,
            file.ProcessingJob.ArchivedAt.Value, approval,
            new KmV2DocumentClassification(
                new KmV2ClassificationLevel(l1.Id, l1.Name),
                new KmV2ClassificationLevel(l2.Id, l2.Name),
                new KmV2ClassificationLevel(l3.Id, l3.Name)),
            new KmV2Supersedes(prior.DocumentId, prior.Version,
                prior.Sha256.ToLowerInvariant()));
    }

    private async Task<KmV2DocumentJobRequest?> BuildTestReportV2RequestAsync(
        AppDbContext db,
        KmIntegrationOutbox row,
        KmIntegrationOptions options,
        CancellationToken cancellationToken)
    {
        if (row.Version <= 1 || !row.ProjectId.HasValue ||
            !TryGetServiceBaseUri(options, out var serviceBase) ||
            await VerificationCaseDeletionHold.IsActiveAsync(db, row.ProjectId.Value,
                cancellationToken)) return null;
        var file = await db.ModuleCaseFiles.AsNoTracking()
            .Include(x => x.Record).ThenInclude(x => x.Module)
            .SingleOrDefaultAsync(x => x.Id == row.SourceEntityId &&
                x.DocumentId == row.DocumentId && x.VersionNo == row.Version,
                cancellationToken);
        if (file is null || file.FileKind != ModuleCaseFileKinds.TestReport ||
            file.ReviewStatus != TestReportReviewStatuses.Approved || !file.IsRawDataExported ||
            !ValidSha256(file.Sha256) || file.FileSize != row.SizeBytes ||
            await db.PendingSubmissions.AsNoTracking().AnyAsync(x =>
                x.Id == file.Id && x.Status != ApprovalStatus.Committed, cancellationToken))
            return null;
        var prior = await db.ModuleCaseFiles.AsNoTracking()
            .Where(x => x.DocumentId == file.DocumentId && x.VersionNo < file.VersionNo &&
                x.FileKind == ModuleCaseFileKinds.TestReport &&
                x.ReviewStatus == TestReportReviewStatuses.Approved && x.IsRawDataExported &&
                x.Sha256 != null)
            .OrderByDescending(x => x.VersionNo).FirstOrDefaultAsync(cancellationToken);
        if (prior is null || !ValidSha256(prior.Sha256)) return null;
        KmV2DocumentApproval? approval = null;
        var bypassedAt = await db.ApprovalAuditEvents.AsNoTracking()
            .Where(x => x.SubmissionId == file.Id &&
                x.Action == ApprovalAuditActions.ApprovalBypassed)
            .OrderByDescending(x => x.OccurredAt).Select(x => (DateTime?)x.OccurredAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (bypassedAt.HasValue)
            approval = new(KmV2ApprovalModes.ApprovalBypassed, file.Id, Utc(bypassedAt.Value));
        else if (file.ReviewedAt.HasValue && !string.IsNullOrWhiteSpace(file.ReviewedBy))
            approval = new(KmV2ApprovalModes.Approved, file.Id, Utc(file.ReviewedAt.Value));
        if (approval is null || !TryResolveManagedFile(file.FilePath, file.FileName, out var fullPath) ||
            !await FileMatchesAsync(fullPath, file.FileSize, file.Sha256!, cancellationToken))
            return null;
        return CreateV2Request(row,
            new KmV2DocumentJobSource(KmAnalysisSourceTypes.TestReport, file.Id,
                file.Record.Module.Code, null, file.RecordId, null, null),
            file.FileName, file.ContentType, file.FileSize, file.Sha256!,
            new Uri(serviceBase!,
                $"api/integrations/km/v1/documents/{file.DocumentId:D}/versions/{file.VersionNo}/content")
                .AbsoluteUri,
            file.UpdatedAt, approval,
            new KmV2DocumentClassification(null, null, null),
            new KmV2Supersedes(prior.DocumentId, prior.VersionNo,
                prior.Sha256!.ToLowerInvariant()));
    }

    private async Task<KmV2DocumentJobRequest?> BuildVerificationAttachmentRequestAsync(
        AppDbContext db,
        KmIntegrationOutbox row,
        KmIntegrationOptions options,
        CancellationToken cancellationToken)
    {
        if (row.Id == Guid.Empty || row.EventId == Guid.Empty || row.DocumentId == Guid.Empty ||
            row.Version != 1 || !TryGetServiceBaseUri(options, out var serviceBase))
            return null;
        var attachment = await db.VerificationApplicationFiles.AsNoTracking()
            .Include(x => x.VerificationApplication)
            .Include(x => x.DocumentLibraryFile)
            .SingleOrDefaultAsync(x => x.Id == row.SourceEntityId, cancellationToken);
        var application = attachment?.VerificationApplication;
        var file = attachment?.DocumentLibraryFile;
        if (attachment is null || application is null || file is null ||
            application.Status != VerificationApplicationStatus.Accepted ||
            application.AcceptedAt is null || application.ProcessedBy is null ||
            attachment.DocumentLibraryFileId != row.DocumentId || file.Id != row.DocumentId ||
            file.StorageArea != DocumentStorageAreas.Library ||
            file.OriginalFileName != row.FileName || file.FileSize != row.SizeBytes ||
            !ValidSha256(attachment.Sha256) || !ValidSha256(file.Sha256) ||
            !string.Equals(attachment.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase) ||
            await DocumentLibraryFileDeletionHold.ExistsAsync(db, row.DocumentId, cancellationToken) ||
            application.ModuleRecordId is { } recordId &&
            await VerificationCaseDeletionHold.IsActiveAsync(db, recordId, cancellationToken))
            return null;
        if (!TryResolveLibraryFile(file, out var fullPath) ||
            !await FileMatchesAsync(fullPath, file.FileSize, file.Sha256, cancellationToken))
            return null;

        var downloadPath = $"api/integrations/km/v1/documents/{file.Id:D}/versions/1/content";
        return CreateV2Request(row,
            new KmV2DocumentJobSource(
                KmAnalysisSourceTypes.VerificationApplicationAttachment,
                attachment.Id,
                "verification-applications",
                application.Id,
                application.ModuleRecordId,
                null,
                null),
            file.OriginalFileName,
            file.ContentType,
            file.FileSize,
            file.Sha256,
            new Uri(serviceBase!, downloadPath).AbsoluteUri,
            row.CommittedAt,
            new KmV2DocumentApproval(
                KmV2ApprovalModes.Approved,
                application.Id,
                Utc(application.AcceptedAt.Value)));
    }

    private static async Task<KmV2DocumentJobRequest?> BuildVendorRequestAsync(
        AppDbContext db,
        KmIntegrationOutbox row,
        KmIntegrationOptions options,
        CancellationToken cancellationToken)
    {
        if (!options.VendorEnabled || row.Id == Guid.Empty || row.EventId == Guid.Empty || row.DocumentId == Guid.Empty ||
            row.EventId != row.SourceEntityId || row.DocumentId != row.SourceEntityId || row.Version != 1 ||
            !TryGetServiceBaseUri(options, out var serviceBase))
            return null;
        if (await db.VendorFileDeletionJobs.AsNoTracking().AnyAsync(x =>
            x.VendorFileId == row.SourceEntityId, cancellationToken)) return null;
        var file = await db.VendorFiles.AsNoTracking().Include(x => x.Vendor)
            .SingleOrDefaultAsync(x => x.Id == row.SourceEntityId, cancellationToken);
        if (file?.Vendor is null || file.ArchiveStatus != FileArchiveStatuses.Archived ||
            file.ApprovalOperationId is null || file.ApprovedAt is null ||
            string.IsNullOrWhiteSpace(file.ApprovedBy) || !ValidSha256(file.Sha256) ||
            file.OriginalFileName != row.FileName || file.FileSize != row.SizeBytes)
            return null;
        var approval = await FindApprovalEvidenceAsync(db, file.ApprovalOperationId.Value,
            "vendor", cancellationToken);
        if (approval is null) return null;
        string fullPath;
        try { fullPath = ManagedAssetFileStorage.FullPath(file.FolderPath, file.StoredFileName); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return null; }
        if (!await FileMatchesAsync(fullPath, file.FileSize, file.Sha256!, cancellationToken))
            return null;
        var downloadPath = $"api/integrations/km/v1/vendor-files/{file.Id:D}/content";
        return CreateV2Request(row,
            new KmV2DocumentJobSource(
                KmAnalysisSourceTypes.VendorFile,
                file.Id,
                "vendor",
                null,
                null,
                file.VendorId,
                null),
            file.OriginalFileName,
            file.ContentType,
            file.FileSize,
            file.Sha256!,
            new Uri(serviceBase!, downloadPath).AbsoluteUri,
            file.ArchiveUpdatedAt,
            approval);
    }

    private static async Task<KmV2DocumentJobRequest?> BuildEquipmentRequestAsync(
        AppDbContext db,
        KmIntegrationOutbox row,
        KmIntegrationOptions options,
        CancellationToken cancellationToken)
    {
        if (!options.EquipmentEnabled || row.Id == Guid.Empty || row.EventId == Guid.Empty || row.DocumentId == Guid.Empty ||
            row.EventId != row.SourceEntityId || row.DocumentId != row.SourceEntityId || row.Version != 1 ||
            !TryGetServiceBaseUri(options, out var serviceBase))
            return null;
        if (await db.EquipmentFileDeletionJobs.AsNoTracking().AnyAsync(x =>
            x.ApparatusFileId == row.SourceEntityId, cancellationToken))
            return null;
        var file = await db.ApparatusFiles.AsNoTracking().Include(x => x.Apparatus).ThenInclude(x => x.Vendor)
            .SingleOrDefaultAsync(x => x.Id == row.SourceEntityId, cancellationToken);
        var originalName = file?.OriginalFileName ?? file?.FileName;
        var isImage = file is not null && ManagedAssetFileStorage.IsImage(file.ContentType,
            originalName ?? string.Empty);
        var expectedEventType = isImage
            ? KmIntegrationEventTypes.EquipmentAttachmentArchived
            : KmIntegrationEventTypes.EquipmentDocumentArchived;
        if (file?.Apparatus is null || file.Apparatus.ModuleCode != "equipment" ||
            file.ArchiveStatus != FileArchiveStatuses.Archived ||
            row.EventType != expectedEventType || originalName != row.FileName ||
            !ValidSha256(file.Sha256) || file.FileSize != row.SizeBytes)
            return null;
        var approval = await FindApprovalEvidenceAsync(db, file.Id, "equipment", cancellationToken);
        if (approval is null || !TryResolveManagedFile(file.FilePath, file.FileName, out var fullPath) ||
            !await FileMatchesAsync(fullPath, file.FileSize, file.Sha256!, cancellationToken)) return null;
        var downloadPath = $"api/integrations/km/v1/equipment-documents/{file.Id:D}/content";
        return CreateV2Request(row,
            new KmV2DocumentJobSource(
                KmAnalysisSourceTypes.EquipmentAttachment,
                file.Id,
                "equipment",
                null,
                null,
                file.Apparatus.VendorId,
                file.Apparatus.Id),
            originalName!,
            file.ContentType,
            file.FileSize,
            file.Sha256!,
            new Uri(serviceBase!, downloadPath).AbsoluteUri,
            file.ArchiveUpdatedAt ?? file.CreatedAt,
            approval);
    }

    private static KmV2DocumentJobRequest CreateV2Request(
        KmIntegrationOutbox row,
        KmV2DocumentJobSource source,
        string originalFileName,
        string? contentType,
        long sizeBytes,
        string sha256,
        string downloadUrl,
        DateTime archivedAt,
        KmV2DocumentApproval approval,
        KmV2DocumentClassification? classification = null,
        KmV2Supersedes? supersedes = null)
    {
        return new KmV2DocumentJobRequest(
            "2.0",
            row.EventId,
            row.Id,
            row.EventType,
            "CSIT",
            source,
            new KmV2Document(
                row.DocumentId,
                row.Version,
                originalFileName,
                string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
                sizeBytes,
                sha256.ToLowerInvariant(),
                downloadUrl,
                Utc(archivedAt),
                approval),
            classification ?? new KmV2DocumentClassification(null, null, null),
            supersedes,
            Utc(row.CreatedAt));
    }

    private static async Task<KmV2DocumentApproval?> FindApprovalEvidenceAsync(
        AppDbContext db,
        Guid referenceId,
        string moduleCode,
        CancellationToken cancellationToken)
    {
        var approved = await db.PendingSubmissions.AsNoTracking()
            .Where(x => x.Id == referenceId && x.SourceModuleCode == moduleCode &&
                x.Status == ApprovalStatus.Committed && x.ReviewerAccount != null &&
                x.ReviewedAt != null)
            .Select(x => x.ReviewedAt)
            .SingleOrDefaultAsync(cancellationToken);
        if (approved.HasValue)
            return new KmV2DocumentApproval(KmV2ApprovalModes.Approved,
                referenceId, Utc(approved.Value));

        var bypassed = await db.ApprovalAuditEvents.AsNoTracking()
            .Where(x => x.SubmissionId == referenceId && x.SourceModuleCode == moduleCode &&
                x.Action == ApprovalAuditActions.ApprovalBypassed)
            .OrderByDescending(x => x.OccurredAt)
            .Select(x => (DateTime?)x.OccurredAt)
            .FirstOrDefaultAsync(cancellationToken);
        return bypassed.HasValue
            ? new KmV2DocumentApproval(KmV2ApprovalModes.ApprovalBypassed,
                referenceId, Utc(bypassed.Value))
            : null;
    }

    private bool TryResolveLibraryFile(DocumentLibraryFile file, out string path)
    {
        path = string.Empty;
        var root = documentStorage?.Value.RootPath?.Trim();
        if (string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            var fullRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            path = Path.GetFullPath(Path.Combine(fullRoot, file.RelativePath));
            return path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryResolveManagedFile(string folder, string storedName, out string path)
    {
        path = string.Empty;
        try
        {
            var fullFolder = Path.GetFullPath(folder)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            path = Path.GetFullPath(Path.Combine(fullFolder, storedName));
            return path.StartsWith(fullFolder, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static async Task<bool> FileMatchesAsync(string path, long expectedSize,
        string expectedSha256, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != expectedSize) return false;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 81920, useAsync: true);
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            return string.Equals(digest, expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException)
        {
            return false;
        }
    }

    private static bool ValidSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool TryGetServiceBaseUri(KmIntegrationOptions options, out Uri? serviceBase)
    {
        if (!Uri.TryCreate(options.ServiceBaseUrl, UriKind.Absolute, out serviceBase) ||
            serviceBase.Scheme != Uri.UriSchemeHttps || serviceBase.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(serviceBase.Query) || !string.IsNullOrEmpty(serviceBase.Fragment))
        {
            serviceBase = null;
            return false;
        }
        return true;
    }

    private static DateTime Utc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private async Task EnsurePendingDocumentLibraryOutboxAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;
        if (db.Database.IsRelational())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO km_integration_outbox
                    (id, event_id, event_type, source_system, source_entity_type, source_entity_id,
                     document_id, version, project_id, case_id, task_id, file_name, content_type,
                     size_bytes, committed_at, status, retry_count, created_at, updated_at)
                SELECT gen_random_uuid(), job.id, 'DocumentLibraryFileUploaded', 'CSIT',
                    'DocumentLibraryFile', file.id, file.document_id, file.version, NULL, NULL, NULL,
                    file.original_file_name, COALESCE(NULLIF(file.content_type, ''), 'application/octet-stream'),
                    file.file_size, job.archived_at, 'Pending', 0, {now}, {now}
                FROM document_processing_jobs AS job
                JOIN document_files AS file ON file.id = job.document_file_id
                WHERE job.archive_status = 'Completed' AND file.storage_area = 'Library'
                  AND NOT EXISTS (SELECT 1 FROM document_file_deletion_jobs AS deletion
                    WHERE deletion.document_file_id = file.id)
                  AND (EXISTS (SELECT 1 FROM pending_submissions AS submission
                        WHERE submission.id = file.id
                          AND submission.source_module_code = 'document-library'
                          AND submission.status = 'Committed'
                          AND submission.reviewer_account IS NOT NULL
                          AND submission.reviewed_at IS NOT NULL)
                    OR EXISTS (SELECT 1 FROM approval_audit_events AS audit
                        WHERE audit.submission_id = file.id
                          AND audit.source_module_code = 'document-library'
                          AND audit.action = 'ApprovalBypassed'))
                  AND NOT EXISTS (SELECT 1 FROM km_integration_outbox AS existing
                                  WHERE existing.event_id = job.id)
                ON CONFLICT DO NOTHING
                """, cancellationToken);
            return;
        }

        var pending = await db.DocumentProcessingJobs.Include(x => x.DocumentFile)
            .Where(x => x.ArchiveStatus == DocumentArchiveStatuses.Completed &&
                x.DocumentFile.StorageArea == DocumentStorageAreas.Library &&
                !db.DocumentLibraryFileDeletionJobs.Any(deletion =>
                    deletion.DocumentFileId == x.DocumentFileId) &&
                (db.PendingSubmissions.Any(submission => submission.Id == x.DocumentFileId &&
                     submission.SourceModuleCode == "document-library" &&
                     submission.Status == ApprovalStatus.Committed &&
                     submission.ReviewerAccount != null && submission.ReviewedAt != null) ||
                 db.ApprovalAuditEvents.Any(audit => audit.SubmissionId == x.DocumentFileId &&
                     audit.SourceModuleCode == "document-library" &&
                     audit.Action == ApprovalAuditActions.ApprovalBypassed)) &&
                !db.KmIntegrationOutbox.Any(o => o.EventId == x.Id))
            .ToListAsync(cancellationToken);
        foreach (var job in pending)
            db.KmIntegrationOutbox.Add(KmIntegrationOutbox.ForDocumentLibraryFile(job.DocumentFile, job, now));
        if (pending.Count > 0) await db.SaveChangesAsync(cancellationToken);
    }

    private static void ValidateOptions(KmIntegrationOptions options)
    {
        if (options.MaxRetryCount < 1 || options.LeaseSeconds <= options.TimeoutSeconds + 5 ||
            options.PollSeconds < 1 || options.TimeoutSeconds < 1 ||
            options.RetryScheduleSeconds.Length == 0 || options.RetryScheduleSeconds.Any(x => x < 1))
            throw new InvalidOperationException("KM integration retry or lease configuration is invalid.");
    }
}

public sealed class KmSyncBackgroundService(
    IServiceScopeFactory scopes,
    IOptions<KmIntegrationOptions> configured,
    ILogger<KmSyncBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = Math.Max(1, configured.Value.PollSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!configured.Value.Enabled) continue;
            try
            {
                using var scope = scopes.CreateScope();
                var processed = await scope.ServiceProvider.GetRequiredService<KmSyncWorker>()
                    .ProcessBatchAsync(stoppingToken);
                if (processed > 0)
                {
                    var snapshot = await scope.ServiceProvider.GetRequiredService<KmSyncDiagnostics>()
                        .ReadAsync(stoppingToken);
                    logger.LogInformation("KM outbox summary Pending={Pending} Failed={Failed} RetryCount={RetryCount} LastAttemptAt={LastAttemptAt} LastErrorCode={LastErrorCode}",
                        snapshot.PendingCount, snapshot.FailedCount, snapshot.RetryCount,
                        snapshot.LastAttemptAt, snapshot.LastErrorCode);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "KM synchronization worker failed."); }
        }
    }
}
