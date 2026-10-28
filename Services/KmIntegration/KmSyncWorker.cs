using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public sealed class KmSyncWorker(
    IDbContextFactory<AppDbContext> dbFactory,
    IKmIntegrationClient client,
    IOptions<KmIntegrationOptions> configured,
    ILogger<KmSyncWorker> logger)
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
                WHERE outbox.event_id = job.id AND outbox.document_id = job.document_file_id
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
                  OR (event_type = 'DocumentLibraryFileUploaded' AND source_entity_type = 'DocumentLibraryFile')
                  OR (@vendor_enabled AND event_type = 'VendorFileArchived' AND source_entity_type = 'VendorFile')
                  OR (@equipment_enabled AND event_type = 'EquipmentDocumentArchived' AND source_entity_type = 'ApparatusFile')) AND
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
                        x.DocumentFileId == expired.DocumentId, cancellationToken);
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
                      x.SourceEntityType == KmIntegrationSourceEntityTypes.DocumentLibraryFile) ||
                     (options.VendorEnabled &&
                      x.EventType == KmIntegrationEventTypes.VendorFileArchived &&
                       x.SourceEntityType == KmIntegrationSourceEntityTypes.VendorFile) ||
                      (options.EquipmentEnabled &&
                       x.EventType == KmIntegrationEventTypes.EquipmentDocumentArchived &&
                       x.SourceEntityType == KmIntegrationSourceEntityTypes.ApparatusFile)) &&
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
                    var request = await BuildDocumentLibraryRequestAsync(db, row, options, deadline.Token);
                    result = request is null
                        ? KmDeliveryResult.Failure(false, "DOCUMENT_UNAVAILABLE",
                            "Staged document library file or classification is unavailable.")
                        : await client.SubmitDocumentLibraryAsync(request, deadline.Token);
                }
                else if (row.EventType == KmIntegrationEventTypes.VendorFileArchived &&
                    row.SourceEntityType == KmIntegrationSourceEntityTypes.VendorFile)
                {
                    var request = await BuildVendorRequestAsync(db, row, options, deadline.Token);
                    result = request is null
                        ? KmDeliveryResult.Failure(false, "VENDOR_FILE_UNAVAILABLE",
                            "Approved formal Vendor file is unavailable.")
                        : await client.SubmitVendorFileAsync(request, deadline.Token);
                }
                else if (row.EventType == KmIntegrationEventTypes.EquipmentDocumentArchived &&
                    row.SourceEntityType == KmIntegrationSourceEntityTypes.ApparatusFile)
                {
                    var request = await BuildEquipmentRequestAsync(db, row, options, deadline.Token);
                    result = request is null
                        ? KmDeliveryResult.Failure(false, "EQUIPMENT_DOCUMENT_UNAVAILABLE",
                            "Approved formal Equipment document is unavailable.")
                        : await client.SubmitEquipmentDocumentAsync(request, deadline.Token);
                }
                else
                {
                    var request = await BuildRequestAsync(db, row, deadline.Token);
                    result = request is null
                        ? KmDeliveryResult.Failure(false, "DOCUMENT_NOT_COMMITTED", "Formal test report is unavailable.")
                        : await client.SubmitAsync(request, deadline.Token);
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
                x.DocumentFileId == current.DocumentId, cancellationToken);
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
            .SingleOrDefaultAsync(x => x.Id == row.EventId && x.DocumentFileId == row.DocumentId,
                cancellationToken);
        var file = await db.DocumentLibraryFiles.AsNoTracking()
            .Include(x => x.Folder).ThenInclude(x => x.Parent).ThenInclude(x => x!.Parent)
            .SingleOrDefaultAsync(x => x.Id == row.DocumentId, cancellationToken);
        if (job is null || file is null || file.StorageArea != DocumentStorageAreas.Staging ||
            file.FileSize != row.SizeBytes || file.Sha256.Length != 64 ||
            !file.Sha256.All(Uri.IsHexDigit) || file.Sha256.Any(char.IsUpper) ||
            file.Folder.Level != 3 || file.Folder.Parent?.Level != 2 ||
            file.Folder.Parent.Parent?.Level != 1)
            return null;
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

    private static async Task<VendorFileIngestionRequest?> BuildVendorRequestAsync(
        AppDbContext db,
        KmIntegrationOutbox row,
        KmIntegrationOptions options,
        CancellationToken cancellationToken)
    {
        if (!options.VendorEnabled || row.EventId == Guid.Empty || row.DocumentId == Guid.Empty ||
            row.EventId != row.SourceEntityId || row.DocumentId != row.SourceEntityId || row.Version != 1 ||
            !Uri.TryCreate(options.ServiceBaseUrl, UriKind.Absolute, out var serviceBase) ||
            serviceBase.Scheme != Uri.UriSchemeHttps || serviceBase.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(serviceBase.Query) || !string.IsNullOrEmpty(serviceBase.Fragment))
            return null;
        var file = await db.VendorFiles.AsNoTracking().Include(x => x.Vendor)
            .SingleOrDefaultAsync(x => x.Id == row.SourceEntityId, cancellationToken);
        if (file?.Vendor is null || file.ArchiveStatus != FileArchiveStatuses.Archived ||
            file.ApprovalOperationId is null || file.ApprovedAt is null ||
            string.IsNullOrWhiteSpace(file.ApprovedBy) || file.Sha256 is null ||
            file.Sha256.Length != 64 || file.FileSize != row.SizeBytes)
            return null;
        var downloadPath = $"api/integrations/km/v1/vendor-files/{file.Id:D}/content";
        return new VendorFileIngestionRequest(
            "1.0", row.EventId, KmIntegrationEventTypes.VendorFileArchived,
            "CSIT", file.Id, 1, row.CommittedAt,
            new VendorKmReference(file.Vendor.Id, file.Vendor.Name),
            new VendorKmFile(file.Id, file.OriginalFileName, file.StoredFileName,
                file.ContentType ?? "application/octet-stream", file.FileSize, file.Sha256,
                new Uri(serviceBase, downloadPath).AbsoluteUri));
    }

    private static async Task<EquipmentDocumentIngestionRequest?> BuildEquipmentRequestAsync(
        AppDbContext db,
        KmIntegrationOutbox row,
        KmIntegrationOptions options,
        CancellationToken cancellationToken)
    {
        if (!options.EquipmentEnabled || row.EventId == Guid.Empty || row.DocumentId == Guid.Empty ||
            row.EventId != row.SourceEntityId || row.DocumentId != row.SourceEntityId || row.Version != 1 ||
            !Uri.TryCreate(options.ServiceBaseUrl, UriKind.Absolute, out var serviceBase) ||
            serviceBase.Scheme != Uri.UriSchemeHttps || serviceBase.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(serviceBase.Query) || !string.IsNullOrEmpty(serviceBase.Fragment))
            return null;
        var file = await db.ApparatusFiles.AsNoTracking().Include(x => x.Apparatus).ThenInclude(x => x.Vendor)
            .SingleOrDefaultAsync(x => x.Id == row.SourceEntityId, cancellationToken);
        var originalName = file?.OriginalFileName ?? file?.FileName;
        if (file?.Apparatus is null || file.Apparatus.ModuleCode != "equipment" ||
            file.ArchiveStatus != FileArchiveStatuses.Archived ||
            ManagedAssetFileStorage.IsImage(file.ContentType, originalName ?? string.Empty) ||
            file.Sha256 is null || file.Sha256.Length != 64 || file.FileSize != row.SizeBytes)
            return null;
        var downloadPath = $"api/integrations/km/v1/equipment-documents/{file.Id:D}/content";
        return new EquipmentDocumentIngestionRequest(
            "1.0", row.EventId, KmIntegrationEventTypes.EquipmentDocumentArchived,
            "CSIT", file.Id, 1, row.CommittedAt,
            new EquipmentKmReference(file.Apparatus.Id, file.Apparatus.ProductsId,
                file.Apparatus.Name, file.Apparatus.Kind, file.Apparatus.Brand,
                file.Apparatus.VendorId, file.Apparatus.Vendor?.Name, file.Apparatus.Model),
            new EquipmentKmFile(file.Id, originalName!, file.FileName,
                file.ContentType ?? "application/octet-stream", file.FileSize, file.Sha256,
                new Uri(serviceBase, downloadPath).AbsoluteUri, EquipmentDocumentKind(originalName!)));
    }

    private static string EquipmentDocumentKind(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (name.Contains("datasheet", StringComparison.OrdinalIgnoreCase) || name.Contains("spec", StringComparison.OrdinalIgnoreCase))
            return "Datasheet";
        if (name.Contains("sop", StringComparison.OrdinalIgnoreCase)) return "SOP";
        if (name.Contains("manual", StringComparison.OrdinalIgnoreCase) || name.Contains("手冊", StringComparison.OrdinalIgnoreCase))
            return "Manual";
        return "EquipmentDocument";
    }

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
                    'DocumentLibraryFile', file.id, file.id, 1, NULL, NULL, NULL,
                    file.original_file_name, COALESCE(NULLIF(file.content_type, ''), 'application/octet-stream'),
                    file.file_size, job.created_at, 'Pending', 0, {now}, {now}
                FROM document_processing_jobs AS job
                JOIN document_files AS file ON file.id = job.document_file_id
                WHERE job.status = 'Pending' AND file.storage_area = 'Staging'
                  AND NOT EXISTS (SELECT 1 FROM km_integration_outbox AS existing
                                  WHERE existing.event_id = job.id)
                ON CONFLICT DO NOTHING
                """, cancellationToken);
            return;
        }

        var pending = await db.DocumentProcessingJobs.Include(x => x.DocumentFile)
            .Where(x => x.Status == DocumentProcessingStatuses.Pending &&
                x.DocumentFile.StorageArea == DocumentStorageAreas.Staging &&
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
