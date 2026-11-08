using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public sealed class KmAnalysisCallbackException(int statusCode, string error) : Exception(error)
{
    public int StatusCode { get; } = statusCode;
    public string Error { get; } = error;
}

public sealed class KmAnalysisCallbackService(IDbContextFactory<AppDbContext> dbFactory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task ApplyDocumentStatusAsync(KmV2DocumentJobStatusCallback callback,
        CancellationToken cancellationToken = default)
    {
        ValidateEnvelope(callback);
        var hash = PayloadHash(callback);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken) : null;

        if (await IsDuplicateAsync(db, callback.CallbackId, callback.CsitJobId,
                "document-jobs/status", callback.Status, hash, cancellationToken)) return;
        var outbox = await LockOutboxAsync(db, callback.CsitJobId, cancellationToken);
        if (outbox is null || outbox.EventId != callback.EventId || outbox.Id != callback.CsitJobId ||
            outbox.KmJobId != callback.KmJobId || outbox.SourceSystem != callback.SourceSystem ||
            outbox.SourceEntityId != callback.SourceEntityId || outbox.DocumentId != callback.DocumentId ||
            outbox.Version != callback.Version || !MatchesSource(outbox, callback.SourceType))
            throw Conflict("callback_mismatch");

        if (await IsDuplicateAsync(db, callback.CallbackId, callback.CsitJobId,
                "document-jobs/status", callback.Status, hash, cancellationToken)) return;
        var source = await ValidateFormalSourceAsync(db, outbox, callback.SourceType, cancellationToken);
        if (!string.Equals(source.Sha256, callback.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw Conflict("source_sha256_mismatch");
        await RejectLateVersionAsync(db, callback.SourceType, callback.DocumentId,
            callback.Version, cancellationToken);

        KmValidatedMetadata? metadata = null;
        if (callback.Status == "Completed" &&
            (!callback.Result.HasValue || !KmAnalysisMetadataValidator.TryValidate(
                callback.Result.Value, "2.0", out metadata)))
            throw BadRequest("invalid_analysis_result");

        var now = DateTime.UtcNow;
        try
        {
            switch (callback.Status)
            {
                case "Processing": outbox.CallbackProcessing(now); break;
                case "Failed": outbox.CallbackFailed(callback.Error!.Code, callback.Error.Message, now); break;
                case "Completed": outbox.CallbackCompleted(now); break;
            }
        }
        catch (InvalidOperationException) { throw Conflict("invalid_transition"); }

        if (callback.SourceType == KmAnalysisSourceTypes.DocumentLibraryFile)
        {
            var job = await db.DocumentProcessingJobs.SingleAsync(x =>
                x.Id == outbox.EventId && x.DocumentFileId == outbox.SourceEntityId,
                cancellationToken);
            try
            {
                if (callback.Status == "Processing")
                    job.CallbackProcessing(callback.Timestamps!.StartedAt!.Value.UtcDateTime, now);
                else if (callback.Status == "Failed")
                    job.CallbackFailed(callback.Timestamps!.StartedAt?.UtcDateTime,
                        callback.Timestamps.FailedAt!.Value.UtcDateTime,
                        callback.Error!.Code, callback.Error.Message,
                        callback.Error.Retryable!.Value, now);
                else
                    job.CallbackCompleted(callback.Timestamps!.StartedAt?.UtcDateTime,
                        callback.Timestamps.CompletedAt!.Value.UtcDateTime,
                        metadata!.Json, now);
            }
            catch (InvalidOperationException) { throw Conflict("invalid_transition"); }
        }

        db.KmAnalysisCallbackReceipts.Add(Receipt(callback.CallbackId, outbox,
            "document-jobs/status", callback.Status, hash, now));
        if (metadata is not null)
        {
            var previous = await LatestResultAsync(db, callback.SourceType,
                callback.SourceEntityId, callback.Version, cancellationToken);
            if (previous is not null) throw Conflict("analysis_result_already_exists");
            var supersedes = await db.KmDocumentAnalysisResults
                .Where(x => x.SourceType == callback.SourceType &&
                    x.DocumentId == callback.DocumentId && x.Version < callback.Version &&
                    x.SupersededAt == null)
                .OrderByDescending(x => x.Version).ThenByDescending(x => x.SourceRevision)
                .FirstOrDefaultAsync(cancellationToken);
            db.KmDocumentAnalysisResults.Add(KmDocumentAnalysisResult.Create(
                callback.CallbackId, callback.CallbackId, outbox, callback.SourceType,
                callback.SourceEntityId, callback.DocumentId, callback.Version, callback.CsitJobId,
                callback.KmJobId, callback.SourceSha256, metadata.MetadataSchemaVersion,
                metadata.Json, callback.Timestamps!.CompletedAt!.Value.UtcDateTime,
                metadata.IndexedAt, now, 1, hash, supersedes?.Id));
            supersedes?.MarkSuperseded(now);
        }
        await SaveAsync(db, transaction, cancellationToken);
    }

    public async Task ApplyTestReportResultAsync(KmV2AnalysisResultCallback callback,
        CancellationToken cancellationToken = default)
    {
        if (callback is null || callback.SchemaVersion != "2.0" ||
            callback.AnalysisResultId == Guid.Empty || callback.CallbackId == Guid.Empty ||
            callback.SourceSystem != "CSIT" || callback.SourceType != KmAnalysisSourceTypes.TestReport ||
            callback.SourceEntityId == Guid.Empty || callback.DocumentId == Guid.Empty ||
            callback.Version < 1 || callback.CsitJobId == Guid.Empty ||
            string.IsNullOrWhiteSpace(callback.KmJobId) || !Sha256(callback.SourceSha256) ||
            callback.MetadataSchemaVersion != "2.0" || callback.AnalyzedAt.Offset != TimeSpan.Zero ||
            !KmAnalysisMetadataValidator.TryValidate(callback.Result, "2.0", out var metadata))
            throw BadRequest("invalid_callback");

        var hash = PayloadHash(callback);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken) : null;
        if (await IsDuplicateAsync(db, callback.CallbackId, callback.CsitJobId,
                "analysis-results", "Completed", hash, cancellationToken)) return;
        var outbox = await LockOutboxAsync(db, callback.CsitJobId, cancellationToken);
        if (outbox is null || outbox.EventType != KmIntegrationEventTypes.TestReportCommitted ||
            outbox.SourceEntityType != KmIntegrationSourceEntityTypes.TestReport ||
            outbox.SourceEntityId != callback.SourceEntityId || outbox.DocumentId != callback.DocumentId ||
            outbox.Version != callback.Version || outbox.KmJobId != callback.KmJobId ||
            outbox.Status != KmIntegrationStatus.Completed)
            throw Conflict(outbox is not null && outbox.Status != KmIntegrationStatus.Completed
                ? "job_not_completed" : "callback_mismatch");
        if (await IsDuplicateAsync(db, callback.CallbackId, callback.CsitJobId,
                "analysis-results", "Completed", hash, cancellationToken)) return;
        var source = await ValidateFormalSourceAsync(db, outbox, callback.SourceType, cancellationToken);
        if (!string.Equals(source.Sha256, callback.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw Conflict("source_sha256_mismatch");
        await RejectLateVersionAsync(db, callback.SourceType, callback.DocumentId,
            callback.Version, cancellationToken);
        if (await db.KmDocumentAnalysisResults.AsNoTracking().AnyAsync(x =>
                x.AnalysisResultId == callback.AnalysisResultId, cancellationToken))
            throw Conflict("analysis_result_conflict");

        KmDocumentAnalysisResult? supersedes = null;
        if (callback.SupersedesAnalysisResultId.HasValue)
        {
            supersedes = await db.KmDocumentAnalysisResults.SingleOrDefaultAsync(x =>
                x.AnalysisResultId == callback.SupersedesAnalysisResultId.Value, cancellationToken);
            if (supersedes is null || supersedes.SourceType != callback.SourceType ||
                supersedes.DocumentId != callback.DocumentId || supersedes.Version > callback.Version)
                throw Conflict("supersedes_mismatch");
        }
        var revision = (await db.KmDocumentAnalysisResults.Where(x =>
                x.SourceType == callback.SourceType && x.SourceEntityId == callback.SourceEntityId &&
                x.DocumentId == callback.DocumentId && x.Version == callback.Version)
            .Select(x => (int?)x.SourceRevision).MaxAsync(cancellationToken) ?? 0) + 1;
        var now = DateTime.UtcNow;
        db.KmAnalysisCallbackReceipts.Add(Receipt(callback.CallbackId, outbox,
            "analysis-results", "Completed", hash, now));
        db.KmDocumentAnalysisResults.Add(KmDocumentAnalysisResult.Create(
            callback.AnalysisResultId, callback.CallbackId, outbox, callback.SourceType,
            callback.SourceEntityId, callback.DocumentId, callback.Version, callback.CsitJobId,
            callback.KmJobId, callback.SourceSha256, callback.MetadataSchemaVersion,
            metadata!.Json, callback.AnalyzedAt.UtcDateTime, metadata.IndexedAt, now, revision,
            hash, supersedes?.Id));
        supersedes?.MarkSuperseded(now);
        await SaveAsync(db, transaction, cancellationToken);
    }

    public static async Task ProjectDocumentLibraryCallbackAsync(AppDbContext db,
        KmIntegrationOutbox outbox, DocumentLibraryStatusCallback callback,
        string payloadSha256, string? metadataJson, DateTime now,
        CancellationToken cancellationToken)
    {
        db.KmAnalysisCallbackReceipts.Add(Receipt(callback.CallbackId, outbox,
            "document-library/ingestion-status", callback.Status, payloadSha256, now));
        if (callback.Status != "Completed") return;
        var file = await db.DocumentLibraryFiles.AsNoTracking().SingleAsync(x =>
            x.Id == callback.DocumentFileId, cancellationToken);
        var existing = await db.KmDocumentAnalysisResults.AsNoTracking().AnyAsync(x =>
            x.OutboxId == outbox.Id, cancellationToken);
        if (existing) throw Conflict("analysis_result_already_exists");
        db.KmDocumentAnalysisResults.Add(KmDocumentAnalysisResult.Create(
            callback.CallbackId, callback.CallbackId, outbox,
            KmAnalysisSourceTypes.DocumentLibraryFile, file.Id,
            file.DocumentId == Guid.Empty ? file.Id : file.DocumentId,
            file.Version < 1 ? 1 : file.Version,
            callback.CsitJobId, callback.KmJobId, file.Sha256,
            callback.Result!.MetadataSchemaVersion, metadataJson!,
            callback.Timestamps!.CompletedAt!.Value.UtcDateTime,
            callback.Result.Indexing.IndexedAt.UtcDateTime, now, 1, payloadSha256));
    }

    private static void ValidateEnvelope(KmV2DocumentJobStatusCallback callback)
    {
        if (callback is null || callback.SchemaVersion != "2.0" || callback.CallbackId == Guid.Empty ||
            callback.EventId == Guid.Empty || callback.CsitJobId == Guid.Empty ||
            string.IsNullOrWhiteSpace(callback.KmJobId) || callback.SourceSystem != "CSIT" ||
            callback.SourceType is not (KmAnalysisSourceTypes.TestReport or
                KmAnalysisSourceTypes.DocumentLibraryFile or
                KmAnalysisSourceTypes.VerificationApplicationAttachment or
                KmAnalysisSourceTypes.VendorFile or KmAnalysisSourceTypes.EquipmentAttachment) ||
            callback.SourceEntityId == Guid.Empty || callback.DocumentId == Guid.Empty || callback.Version < 1 ||
            !Sha256(callback.SourceSha256) || callback.Status is not ("Processing" or "Completed" or "Failed") ||
            callback.Timestamps is null || !Utc(callback.Timestamps.StartedAt) ||
            !Utc(callback.Timestamps.CompletedAt) || !Utc(callback.Timestamps.FailedAt))
            throw BadRequest("invalid_callback");
        var valid = callback.Status switch
        {
            "Processing" => callback.Timestamps.StartedAt.HasValue &&
                !callback.Timestamps.CompletedAt.HasValue && !callback.Timestamps.FailedAt.HasValue &&
                !callback.Result.HasValue && callback.Error is null,
            "Completed" => callback.Timestamps.CompletedAt.HasValue &&
                !callback.Timestamps.FailedAt.HasValue && callback.Result.HasValue && callback.Error is null,
            _ => callback.Timestamps.FailedAt.HasValue && !callback.Timestamps.CompletedAt.HasValue &&
                !callback.Result.HasValue && callback.Error is not null &&
                !string.IsNullOrWhiteSpace(callback.Error.Code) &&
                !string.IsNullOrWhiteSpace(callback.Error.Message) && callback.Error.Retryable.HasValue
        };
        if (!valid) throw BadRequest("invalid_callback");
    }

    private static bool MatchesSource(KmIntegrationOutbox outbox, string sourceType) => sourceType switch
    {
        KmAnalysisSourceTypes.VerificationApplicationAttachment =>
            outbox.EventType == KmIntegrationEventTypes.VerificationApplicationAttachmentArchived &&
            outbox.SourceEntityType == KmIntegrationSourceEntityTypes.VerificationApplicationFile,
        KmAnalysisSourceTypes.VendorFile => outbox.EventType == KmIntegrationEventTypes.VendorFileArchived &&
            outbox.SourceEntityType == KmIntegrationSourceEntityTypes.VendorFile,
        KmAnalysisSourceTypes.EquipmentAttachment =>
            outbox.EventType is KmIntegrationEventTypes.EquipmentDocumentArchived or
                KmIntegrationEventTypes.EquipmentAttachmentArchived &&
            outbox.SourceEntityType == KmIntegrationSourceEntityTypes.ApparatusFile,
        KmAnalysisSourceTypes.TestReport =>
            outbox.EventType == KmIntegrationEventTypes.TestReportCommitted &&
            outbox.SourceEntityType == KmIntegrationSourceEntityTypes.TestReport &&
            outbox.Version > 1,
        KmAnalysisSourceTypes.DocumentLibraryFile =>
            outbox.EventType == KmIntegrationEventTypes.DocumentLibraryFileUploaded &&
            outbox.SourceEntityType == KmIntegrationSourceEntityTypes.DocumentLibraryFile &&
            outbox.Version > 1,
        _ => false
    };

    private static async Task<FormalSource> ValidateFormalSourceAsync(AppDbContext db,
        KmIntegrationOutbox outbox, string sourceType, CancellationToken cancellationToken)
    {
        switch (sourceType)
        {
            case KmAnalysisSourceTypes.TestReport:
            {
                var file = await db.ModuleCaseFiles.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.Id == outbox.SourceEntityId && x.DocumentId == outbox.DocumentId &&
                    x.VersionNo == outbox.Version, cancellationToken);
                if (file is null) throw Gone("source_deleted");
                if (file.FileKind != ModuleCaseFileKinds.TestReport ||
                    file.ReviewStatus != TestReportReviewStatuses.Approved || !file.IsRawDataExported ||
                    string.IsNullOrWhiteSpace(file.Sha256)) throw Unprocessable("source_not_formal");
                if (await VerificationCaseDeletionHold.IsActiveAsync(db, file.RecordId, cancellationToken))
                    throw Unprocessable("deletion_in_progress");
                return new(file.Sha256);
            }
            case KmAnalysisSourceTypes.DocumentLibraryFile:
            {
                var file = await db.DocumentLibraryFiles.AsNoTracking().Include(x => x.ProcessingJob)
                    .SingleOrDefaultAsync(x => x.Id == outbox.SourceEntityId,
                        cancellationToken);
                if (file is null) throw Gone("source_deleted");
                if (file.DocumentId != outbox.DocumentId || file.Version != outbox.Version ||
                    file.StorageArea != DocumentStorageAreas.Library ||
                    file.ProcessingJob?.Id != outbox.EventId ||
                    file.ProcessingJob.ArchiveStatus != DocumentArchiveStatuses.Completed ||
                    string.IsNullOrWhiteSpace(file.Sha256))
                    throw Unprocessable("source_not_formal");
                if (await DocumentLibraryFileDeletionHold.ExistsAsync(db, file.Id,
                        cancellationToken))
                    throw Unprocessable("deletion_in_progress");
                return new(file.Sha256);
            }
            case KmAnalysisSourceTypes.VerificationApplicationAttachment:
            {
                var file = await db.VerificationApplicationFiles.AsNoTracking()
                    .Include(x => x.VerificationApplication).Include(x => x.DocumentLibraryFile)
                    .SingleOrDefaultAsync(x => x.Id == outbox.SourceEntityId, cancellationToken);
                if (file is null) throw Gone("source_deleted");
                if (file.VerificationApplication.Status != VerificationApplicationStatus.Accepted ||
                    file.DocumentLibraryFileId != outbox.DocumentId || file.DocumentLibraryFile is null ||
                    file.DocumentLibraryFile.StorageArea != DocumentStorageAreas.Library ||
                    string.IsNullOrWhiteSpace(file.Sha256)) throw Unprocessable("source_not_formal");
                if (await DocumentLibraryFileDeletionHold.ExistsAsync(db, outbox.DocumentId,
                        cancellationToken))
                    throw Unprocessable("deletion_in_progress");
                if (file.VerificationApplication.ModuleRecordId is { } recordId &&
                    await VerificationCaseDeletionHold.IsActiveAsync(db, recordId, cancellationToken))
                    throw Unprocessable("deletion_in_progress");
                return new(file.Sha256);
            }
            case KmAnalysisSourceTypes.VendorFile:
            {
                var file = await db.VendorFiles.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.Id == outbox.SourceEntityId && x.Id == outbox.DocumentId, cancellationToken);
                if (file is null) throw Gone("source_deleted");
                var approved = file.ApprovalOperationId.HasValue &&
                    (await db.PendingSubmissions.AsNoTracking().AnyAsync(x =>
                         x.Id == file.ApprovalOperationId.Value && x.SourceModuleCode == "vendor" &&
                         x.Status == ApprovalStatus.Committed && x.ReviewerAccount != null &&
                         x.ReviewedAt != null, cancellationToken) ||
                     await db.ApprovalAuditEvents.AsNoTracking().AnyAsync(x =>
                         x.SubmissionId == file.ApprovalOperationId.Value &&
                         x.SourceModuleCode == "vendor" &&
                         x.Action == ApprovalAuditActions.ApprovalBypassed, cancellationToken));
                if (file.ArchiveStatus != FileArchiveStatuses.Archived || file.ApprovalOperationId is null ||
                    file.ApprovedAt is null || string.IsNullOrWhiteSpace(file.ApprovedBy) ||
                    string.IsNullOrWhiteSpace(file.Sha256) || !approved)
                    throw Unprocessable("source_not_formal");
                if (await db.VendorFileDeletionJobs.AsNoTracking().AnyAsync(x =>
                    x.VendorFileId == file.Id, cancellationToken))
                    throw Unprocessable("deletion_in_progress");
                return new(file.Sha256);
            }
            case KmAnalysisSourceTypes.EquipmentAttachment:
            {
                var file = await db.ApparatusFiles.AsNoTracking().Include(x => x.Apparatus)
                    .SingleOrDefaultAsync(x => x.Id == outbox.SourceEntityId, cancellationToken);
                if (file?.Apparatus is null) throw Gone("source_deleted");
                var approved = await db.PendingSubmissions.AsNoTracking().AnyAsync(x =>
                        x.Id == file.Id && x.SourceModuleCode == "equipment" &&
                        x.Status == ApprovalStatus.Committed &&
                        x.ReviewerAccount != null && x.ReviewedAt != null, cancellationToken) ||
                    await db.ApprovalAuditEvents.AsNoTracking().AnyAsync(x =>
                        x.SubmissionId == file.Id && x.SourceModuleCode == "equipment" &&
                        x.Action == ApprovalAuditActions.ApprovalBypassed,
                        cancellationToken);
                if (file.Apparatus.ModuleCode != "equipment" || file.ArchiveStatus != FileArchiveStatuses.Archived ||
                    string.IsNullOrWhiteSpace(file.Sha256) || !approved)
                    throw Unprocessable("source_not_formal");
                if (await db.EquipmentFileDeletionJobs.AsNoTracking().AnyAsync(x =>
                    x.ApparatusFileId == file.Id,
                    cancellationToken)) throw Unprocessable("deletion_in_progress");
                return new(file.Sha256);
            }
            default: throw Conflict("source_type_mismatch");
        }
    }

    private static async Task<bool> IsDuplicateAsync(AppDbContext db, Guid callbackId, Guid outboxId,
        string endpoint, string status, string hash, CancellationToken cancellationToken)
    {
        var receipt = await db.KmAnalysisCallbackReceipts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CallbackId == callbackId, cancellationToken);
        if (receipt is null) return false;
        if (receipt.OutboxId == outboxId && receipt.Endpoint == endpoint &&
            receipt.Status == status && receipt.PayloadSha256 == hash) return true;
        throw Conflict("callback_conflict");
    }

    private static Task<KmIntegrationOutbox?> LockOutboxAsync(AppDbContext db, Guid outboxId,
        CancellationToken cancellationToken) => db.Database.IsRelational()
        ? db.KmIntegrationOutbox.FromSqlInterpolated(
            $"SELECT * FROM km_integration_outbox WHERE id = {outboxId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken)
        : db.KmIntegrationOutbox.SingleOrDefaultAsync(x => x.Id == outboxId, cancellationToken);

    private static Task<KmDocumentAnalysisResult?> LatestResultAsync(AppDbContext db,
        string sourceType, Guid sourceEntityId, int version, CancellationToken cancellationToken) =>
        db.KmDocumentAnalysisResults.OrderByDescending(x => x.SourceRevision).FirstOrDefaultAsync(x =>
            x.SourceType == sourceType && x.SourceEntityId == sourceEntityId && x.Version == version,
            cancellationToken);

    private static async Task RejectLateVersionAsync(AppDbContext db, string sourceType,
        Guid documentId, int version, CancellationToken cancellationToken)
    {
        if (await db.KmDocumentAnalysisResults.AsNoTracking().AnyAsync(x =>
            x.SourceType == sourceType && x.DocumentId == documentId && x.Version > version,
            cancellationToken)) throw Conflict("late_callback");
    }

    private static KmAnalysisCallbackReceipt Receipt(Guid callbackId, KmIntegrationOutbox outbox,
        string endpoint, string status, string hash, DateTime now) => new()
    {
        CallbackId = callbackId, OutboxId = outbox.Id, Endpoint = endpoint,
        Status = status, PayloadSha256 = hash, ReceivedAt = now
    };

    private static async Task SaveAsync(AppDbContext db,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction,
        CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException) { throw Conflict("callback_conflict"); }
    }

    private static string PayloadHash<T>(T value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Json)))).ToLowerInvariant();
    private static bool Sha256(string value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool Utc(DateTimeOffset? value) => !value.HasValue || value.Value.Offset == TimeSpan.Zero;
    private static KmAnalysisCallbackException BadRequest(string error) => new(400, error);
    private static KmAnalysisCallbackException Conflict(string error) => new(409, error);
    private static KmAnalysisCallbackException Gone(string error) => new(410, error);
    private static KmAnalysisCallbackException Unprocessable(string error) => new(422, error);
    private sealed record FormalSource(string Sha256);
}
