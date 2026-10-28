using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Config;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Authorize(Policy = KmIntegrationAuthorization.Policy)]
[Route("api/integrations/km/v1")]
public sealed class KmIntegrationController(
    IDbContextFactory<AppDbContext> dbFactory,
    ILogger<KmIntegrationController> logger,
    IOptions<DocumentStorageOptions>? storageOptions = null,
    IOptions<DocumentStagingOptions>? stagingOptions = null) : ControllerBase
{
    private static readonly JsonSerializerOptions CallbackJson = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> DocumentTypes =
        ["Specification", "Manual", "TestReport", "SOP", "MeetingMinutes", "IssueAnalysis", "Presentation", "Other"];
    private static readonly HashSet<string> ExtractionMethods = ["Text", "OCR", "Mixed"];

    [HttpGet("documents/{documentId:guid}/versions/{version:int}/content")]
    [Authorize(Policy = KmIntegrationAuthorization.DocumentReadPolicy)]
    public async Task<IActionResult> Download(Guid documentId, int version, CancellationToken cancellationToken)
    {
        if (documentId == Guid.Empty || version < 1) return NotFound();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var file = await db.ModuleCaseFiles.AsNoTracking().SingleOrDefaultAsync(x =>
            x.DocumentId == documentId && x.VersionNo == version &&
            x.FileKind == ModuleCaseFileKinds.TestReport &&
            x.ReviewStatus == TestReportReviewStatuses.Approved && x.IsRawDataExported,
            cancellationToken);
        if (file is not null &&
            !await VerificationCaseDeletionHold.IsActiveAsync(db, file.RecordId, cancellationToken) &&
            await db.KmIntegrationOutbox.AsNoTracking().AnyAsync(x =>
                x.SourceSystem == "CSIT" && x.EventType == "TestReportCommitted" &&
                x.SourceEntityId == file.Id && x.DocumentId == documentId && x.Version == version,
                cancellationToken) &&
            !await db.PendingSubmissions.AsNoTracking().AnyAsync(x =>
                x.Id == file.Id && x.Status != ApprovalStatus.Committed, cancellationToken))
        {
            var path = Path.Combine(file.FilePath, file.FileName);
            return OpenFile(path, file.ContentType, file.FileName);
        }

        var attachment = await db.VerificationApplicationFiles.AsNoTracking()
            .Include(x => x.VerificationApplication)
            .Include(x => x.DocumentLibraryFile)
            .SingleOrDefaultAsync(x => x.DocumentLibraryFileId == documentId, cancellationToken);
        if (attachment?.DocumentLibraryFile is null ||
            (attachment.VerificationApplication.ModuleRecordId is { } attachmentRecordId &&
             await VerificationCaseDeletionHold.IsActiveAsync(db, attachmentRecordId, cancellationToken)) ||
            attachment.VerificationApplication.Status != VerificationApplicationStatus.Accepted ||
            attachment.DocumentLibraryFile.StorageArea != DocumentStorageAreas.Library ||
            !await db.KmIntegrationOutbox.AsNoTracking().AnyAsync(x =>
                x.SourceSystem == "CSIT" &&
                x.EventType == KmIntegrationEventTypes.VerificationApplicationAttachmentArchived &&
                x.SourceEntityType == KmIntegrationSourceEntityTypes.VerificationApplicationFile &&
                x.SourceEntityId == attachment.Id && x.DocumentId == documentId && x.Version == version,
                cancellationToken))
            return NotFound();

        var root = storageOptions?.Value.RootPath?.Trim();
        if (string.IsNullOrWhiteSpace(root)) return NotFound();
        try
        {
            var fullRoot = Path.GetFullPath(root);
            var path = Path.GetFullPath(Path.Combine(fullRoot, attachment.DocumentLibraryFile.RelativePath));
            var prefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return NotFound();
            return OpenFile(path, attachment.DocumentLibraryFile.ContentType,
                attachment.DocumentLibraryFile.OriginalFileName);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return NotFound();
        }
    }

    [HttpGet("document-library/files/{documentFileId:guid}/content")]
    [Authorize(Policy = KmIntegrationAuthorization.DocumentReadPolicy)]
    public async Task<IActionResult> DownloadDocumentLibraryFile(
        Guid documentFileId,
        CancellationToken cancellationToken)
    {
        if (documentFileId == Guid.Empty) return NotFound();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var file = await db.DocumentLibraryFiles.AsNoTracking()
            .Include(x => x.ProcessingJob)
            .SingleOrDefaultAsync(x => x.Id == documentFileId, cancellationToken);
        if (file?.ProcessingJob is null ||
            !await db.KmIntegrationOutbox.AsNoTracking().AnyAsync(x =>
                x.EventType == KmIntegrationEventTypes.DocumentLibraryFileUploaded &&
                x.SourceEntityType == KmIntegrationSourceEntityTypes.DocumentLibraryFile &&
                x.SourceEntityId == documentFileId && x.DocumentId == documentFileId &&
                x.EventId == file.ProcessingJob.Id && x.KmJobId != null &&
                (x.Status == KmIntegrationStatus.Sent || x.Status == KmIntegrationStatus.Processing ||
                 x.Status == KmIntegrationStatus.Completed || x.Status == KmIntegrationStatus.Failed),
                cancellationToken))
            return NotFound();

        var linkedRecordId = await db.VerificationApplicationFiles.AsNoTracking()
            .Where(x => x.DocumentLibraryFileId == documentFileId)
            .Select(x => x.VerificationApplication.ModuleRecordId)
            .SingleOrDefaultAsync(cancellationToken);
        if (linkedRecordId is { } recordId &&
            await VerificationCaseDeletionHold.IsActiveAsync(db, recordId, cancellationToken))
            return NotFound();

        string path;
        try { path = ResolveDocumentPath(file); }
        catch (InvalidOperationException) { return NotFound(); }
        if (!System.IO.File.Exists(path)) return NotFound();
        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            if (!string.Equals(digest, file.Sha256, StringComparison.Ordinal))
            {
                await stream.DisposeAsync();
                logger.LogWarning("Rejected document library download checksum mismatch DocumentFileId={DocumentFileId}",
                    documentFileId);
                return Conflict(new { error = "checksum_mismatch" });
            }
            stream.Position = 0;
            return File(stream, file.ContentType ?? "application/octet-stream",
                Path.GetFileName(file.OriginalFileName));
        }
        catch (FileNotFoundException) { return NotFound(); }
        catch (DirectoryNotFoundException) { return NotFound(); }
    }

    [HttpPost("ingestion-status")]
    [Authorize(Policy = KmIntegrationAuthorization.StatusWritePolicy)]
    public async Task<IActionResult> Status([FromBody] KmStatusCallback callback,
        CancellationToken cancellationToken)
    {
        if (callback is null || string.IsNullOrWhiteSpace(callback.JobId) ||
            callback.SourceSystem != "CSIT" || callback.DocumentId == Guid.Empty || callback.Version < 1 ||
            callback.Status is not ("Processing" or "Completed" or "Failed") ||
            (callback.Status == "Processing" && callback.Timestamps?.StartedAt is null) ||
            (callback.Status == "Completed" && callback.Timestamps?.CompletedAt is null) ||
            (callback.Status == "Failed" && callback.Timestamps?.FailedAt is null) ||
            (callback.Status == "Failed" &&
             (string.IsNullOrWhiteSpace(callback.Error?.Code) || string.IsNullOrWhiteSpace(callback.Error.Message))))
        {
            logger.LogWarning("Rejected invalid KM callback DocumentId={DocumentId} Version={Version}",
                callback?.DocumentId, callback?.Version);
            return BadRequest(new { error = "invalid_callback" });
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken) : null;
        var row = db.Database.IsRelational()
            ? await db.KmIntegrationOutbox
                .FromSqlInterpolated($"SELECT * FROM km_integration_outbox WHERE km_job_id = {callback.JobId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
            : await db.KmIntegrationOutbox.SingleOrDefaultAsync(x => x.KmJobId == callback.JobId, cancellationToken);
        if (row is null || row.SourceSystem != callback.SourceSystem ||
            row.DocumentId != callback.DocumentId || row.Version != callback.Version)
        {
            logger.LogWarning("Rejected KM callback correlation JobId={JobId} DocumentId={DocumentId} Version={Version}",
                callback.JobId, callback.DocumentId, callback.Version);
            return Conflict(new { error = "callback_mismatch" });
        }
        var recordId = row.ProjectId;
        if (recordId is null &&
            row.SourceEntityType == KmIntegrationSourceEntityTypes.VerificationApplicationFile)
            recordId = await db.VerificationApplicationFiles.AsNoTracking()
                .Where(x => x.Id == row.SourceEntityId)
                .Select(x => x.VerificationApplication.ModuleRecordId)
                .SingleOrDefaultAsync(cancellationToken);
        if (recordId is { } activeRecordId &&
            await VerificationCaseDeletionHold.IsActiveAsync(db, activeRecordId, cancellationToken))
            return Conflict(new { error = "deletion_in_progress" });
        try
        {
            var now = DateTime.UtcNow;
            switch (callback.Status)
            {
                case "Processing": row.CallbackProcessing(now); break;
                case "Completed": row.CallbackCompleted(now); break;
                case "Failed": row.CallbackFailed(callback.Error!.Code, callback.Error.Message, now); break;
            }
        }
        catch (InvalidOperationException)
        {
            logger.LogWarning("Rejected KM callback transition EventId={EventId} DocumentId={DocumentId} Version={Version} KmJobId={KmJobId} Status={Status}",
                row.EventId, row.DocumentId, row.Version, row.KmJobId, callback.Status);
            return Conflict(new { error = "invalid_transition" });
        }
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("KM callback transition EventId={EventId} DocumentId={DocumentId} Version={Version} KmJobId={KmJobId} Status={Status}",
            row.EventId, row.DocumentId, row.Version, row.KmJobId, row.Status);
        return NoContent();
    }

    [HttpGet("vendor-files/{vendorFileId:guid}/content")]
    [Authorize(Policy = KmIntegrationAuthorization.DocumentReadPolicy)]
    public async Task<IActionResult> DownloadVendorFile(
        Guid vendorFileId, CancellationToken cancellationToken)
    {
        if (vendorFileId == Guid.Empty) return NotFound();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var file = await db.VendorFiles.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == vendorFileId, cancellationToken);
        if (file is null || file.ArchiveStatus != FileArchiveStatuses.Archived ||
            file.ApprovalOperationId is null || string.IsNullOrWhiteSpace(file.Sha256) ||
            !await db.KmIntegrationOutbox.AsNoTracking().AnyAsync(x =>
                x.EventType == KmIntegrationEventTypes.VendorFileArchived &&
                x.SourceEntityType == KmIntegrationSourceEntityTypes.VendorFile &&
                x.SourceEntityId == vendorFileId && x.DocumentId == vendorFileId && x.Version == 1 &&
                (x.Status == KmIntegrationStatus.Dispatching || x.Status == KmIntegrationStatus.Sent ||
                 x.Status == KmIntegrationStatus.Processing || x.Status == KmIntegrationStatus.Completed ||
                 x.Status == KmIntegrationStatus.Failed),
                cancellationToken))
            return NotFound();
        string path;
        try { path = ManagedAssetFileStorage.FullPath(file.FolderPath, file.StoredFileName); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return NotFound(); }
        if (!System.IO.File.Exists(path)) return NotFound();
        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            if (!string.Equals(digest, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                await stream.DisposeAsync();
                logger.LogWarning("Rejected Vendor file download checksum mismatch VendorFileId={VendorFileId}",
                    vendorFileId);
                return Conflict(new { error = "checksum_mismatch" });
            }
            stream.Position = 0;
            return File(stream, file.ContentType ?? "application/octet-stream",
                Path.GetFileName(file.OriginalFileName));
        }
        catch (FileNotFoundException) { return NotFound(); }
        catch (DirectoryNotFoundException) { return NotFound(); }
    }

    [HttpGet("equipment-documents/{apparatusFileId:guid}/content")]
    [Authorize(Policy = KmIntegrationAuthorization.DocumentReadPolicy)]
    public async Task<IActionResult> DownloadEquipmentDocument(
        Guid apparatusFileId, CancellationToken cancellationToken)
    {
        if (apparatusFileId == Guid.Empty) return NotFound();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var file = await db.ApparatusFiles.AsNoTracking().Include(x => x.Apparatus)
            .SingleOrDefaultAsync(x => x.Id == apparatusFileId, cancellationToken);
        var originalName = file?.OriginalFileName ?? file?.FileName;
        if (file?.Apparatus is null || file.Apparatus.ModuleCode != "equipment" ||
            file.ArchiveStatus != FileArchiveStatuses.Archived ||
            ManagedAssetFileStorage.IsImage(file.ContentType, originalName ?? string.Empty) ||
            string.IsNullOrWhiteSpace(file.Sha256) ||
            !await db.KmIntegrationOutbox.AsNoTracking().AnyAsync(x =>
                x.EventType == KmIntegrationEventTypes.EquipmentDocumentArchived &&
                x.SourceEntityType == KmIntegrationSourceEntityTypes.ApparatusFile &&
                x.SourceEntityId == apparatusFileId && x.DocumentId == apparatusFileId && x.Version == 1 &&
                (x.Status == KmIntegrationStatus.Dispatching || x.Status == KmIntegrationStatus.Sent ||
                 x.Status == KmIntegrationStatus.Processing || x.Status == KmIntegrationStatus.Completed ||
                 x.Status == KmIntegrationStatus.Failed), cancellationToken))
            return NotFound();
        var path = Path.Combine(file.FilePath, file.FileName);
        if (!System.IO.File.Exists(path)) return NotFound();
        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            if (!string.Equals(digest, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                await stream.DisposeAsync();
                logger.LogWarning("Rejected Equipment document download checksum mismatch ApparatusFileId={ApparatusFileId}",
                    apparatusFileId);
                return Conflict(new { error = "checksum_mismatch" });
            }
            stream.Position = 0;
            return File(stream, file.ContentType ?? "application/octet-stream", Path.GetFileName(originalName));
        }
        catch (FileNotFoundException) { return NotFound(); }
        catch (DirectoryNotFoundException) { return NotFound(); }
    }

    [HttpPost("equipment-documents/ingestion-status")]
    [Authorize(Policy = KmIntegrationAuthorization.StatusWritePolicy)]
    public async Task<IActionResult> EquipmentDocumentStatus(
        [FromBody] EquipmentDocumentStatusCallback callback,
        CancellationToken cancellationToken)
    {
        if (!IsValidEquipmentCallback(callback))
            return BadRequest(new { error = "invalid_callback" });

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken) : null;
        var row = db.Database.IsRelational()
            ? await db.KmIntegrationOutbox
                .FromSqlInterpolated($"SELECT * FROM km_integration_outbox WHERE km_job_id = {callback.JobId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
            : await db.KmIntegrationOutbox.SingleOrDefaultAsync(x => x.KmJobId == callback.JobId,
                cancellationToken);
        if (row is null || row.EventType != KmIntegrationEventTypes.EquipmentDocumentArchived ||
            row.SourceEntityType != KmIntegrationSourceEntityTypes.ApparatusFile ||
            row.SourceSystem != callback.SourceSystem || row.DocumentId != callback.DocumentId ||
            row.SourceEntityId != callback.DocumentId || row.Version != callback.Version)
            return Conflict(new { error = "callback_mismatch" });

        var file = await db.ApparatusFiles.Include(x => x.Apparatus)
            .SingleOrDefaultAsync(x => x.Id == row.SourceEntityId, cancellationToken);
        if (file?.Apparatus is null || file.Apparatus.ModuleCode != "equipment" ||
            file.ArchiveStatus != FileArchiveStatuses.Archived)
            return Conflict(new { error = "document_unavailable" });

        try
        {
            var now = DateTime.UtcNow;
            switch (callback.Status)
            {
                case "Processing":
                    row.CallbackProcessing(now);
                    break;
                case "Completed":
                    var metadata = JsonSerializer.Serialize(callback.Result, CallbackJson);
                    if (row.Status == KmIntegrationStatus.Completed && file.MetadataJson is not null &&
                        !string.Equals(file.MetadataJson, metadata, StringComparison.Ordinal))
                        return Conflict(new { error = "callback_conflict" });
                    row.CallbackCompleted(now);
                    file.MetadataJson ??= metadata;
                    file.MetadataSource ??= "AndersonKM:EquipmentDocument";
                    file.MetadataVersion ??= callback.Result!.MetadataSchemaVersion;
                    if (file.MetadataVerificationStatus is null or MetadataVerificationStatuses.NotApplicable)
                        file.MetadataVerificationStatus = MetadataVerificationStatuses.PendingVerification;
                    break;
                case "Failed":
                    row.CallbackFailed(callback.Error!.Code, callback.Error.Message, now);
                    break;
            }
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException)
        {
            return Conflict(new { error = "invalid_transition" });
        }
    }

    [HttpPost("document-library/ingestion-status")]
    [Authorize(Policy = KmIntegrationAuthorization.StatusWritePolicy)]
    public async Task<IActionResult> DocumentLibraryStatus(
        [FromBody] DocumentLibraryStatusCallback callback,
        CancellationToken cancellationToken)
    {
        if (!IsValidDocumentLibraryCallback(callback))
        {
            logger.LogWarning("Rejected invalid document library callback DocumentFileId={DocumentFileId} CsitJobId={CsitJobId}",
                callback?.DocumentFileId, callback?.CsitJobId);
            return BadRequest(new { error = "invalid_callback" });
        }

        var payloadJson = JsonSerializer.Serialize(callback, CallbackJson);
        var payloadSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson)))
            .ToLowerInvariant();
        var metadataJson = callback.Status == DocumentProcessingStatuses.Completed
            ? JsonSerializer.Serialize(callback.Result, CallbackJson) : null;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken) : null;

        var existingReceipt = await db.DocumentProcessingCallbacks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CallbackId == callback.CallbackId, cancellationToken);
        if (existingReceipt is not null)
        {
            if (existingReceipt.DocumentProcessingJobId == callback.CsitJobId &&
                existingReceipt.Status == callback.Status &&
                existingReceipt.PayloadSha256 == payloadSha256)
                return NoContent();
            return Conflict(new { error = "callback_conflict" });
        }

        var row = db.Database.IsRelational()
            ? await db.KmIntegrationOutbox
                .FromSqlInterpolated($"SELECT * FROM km_integration_outbox WHERE km_job_id = {callback.KmJobId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
            : await db.KmIntegrationOutbox.SingleOrDefaultAsync(x => x.KmJobId == callback.KmJobId,
                cancellationToken);
        if (row is null || row.EventType != KmIntegrationEventTypes.DocumentLibraryFileUploaded ||
            row.SourceEntityType != KmIntegrationSourceEntityTypes.DocumentLibraryFile ||
            row.SourceSystem != callback.SourceSystem || row.DocumentId != callback.DocumentFileId ||
            row.SourceEntityId != callback.DocumentFileId || row.EventId != callback.CsitJobId)
        {
            logger.LogWarning("Rejected document library callback correlation KmJobId={KmJobId} DocumentFileId={DocumentFileId} CsitJobId={CsitJobId}",
                callback.KmJobId, callback.DocumentFileId, callback.CsitJobId);
            return Conflict(new { error = "callback_mismatch" });
        }

        // A concurrent duplicate can pass the optimistic receipt lookup while the
        // first request is waiting for this outbox lock. Re-read after acquiring
        // the lock so the callback remains idempotent instead of surfacing a PK error.
        existingReceipt = await db.DocumentProcessingCallbacks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CallbackId == callback.CallbackId, cancellationToken);
        if (existingReceipt is not null)
        {
            if (existingReceipt.DocumentProcessingJobId == callback.CsitJobId &&
                existingReceipt.Status == callback.Status &&
                existingReceipt.PayloadSha256 == payloadSha256)
                return NoContent();
            return Conflict(new { error = "callback_conflict" });
        }

        var job = db.Database.IsRelational()
            ? await db.DocumentProcessingJobs
                .FromSqlInterpolated($"SELECT * FROM document_processing_jobs WHERE id = {callback.CsitJobId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
            : await db.DocumentProcessingJobs.SingleOrDefaultAsync(x => x.Id == callback.CsitJobId,
                cancellationToken);
        if (job is null || job.DocumentFileId != callback.DocumentFileId)
            return Conflict(new { error = "callback_mismatch" });

        try
        {
            var now = DateTime.UtcNow;
            switch (callback.Status)
            {
                case "Processing":
                    row.CallbackProcessing(now);
                    job.CallbackProcessing(callback.Timestamps!.StartedAt!.Value.UtcDateTime, now);
                    break;
                case "Completed":
                    row.CallbackCompleted(now);
                    job.CallbackCompleted(callback.Timestamps!.StartedAt?.UtcDateTime,
                        callback.Timestamps.CompletedAt!.Value.UtcDateTime, metadataJson!, now);
                    break;
                case "Failed":
                    row.CallbackFailed(callback.Error!.Code, callback.Error.Message, now);
                    job.CallbackFailed(callback.Timestamps!.StartedAt?.UtcDateTime,
                        callback.Timestamps.FailedAt!.Value.UtcDateTime, callback.Error.Code,
                        callback.Error.Message, callback.Error.Retryable!.Value, now);
                    break;
            }
            db.DocumentProcessingCallbacks.Add(new DocumentProcessingCallbackReceipt
            {
                CallbackId = callback.CallbackId,
                DocumentProcessingJobId = job.Id,
                Status = callback.Status,
                PayloadSha256 = payloadSha256,
                ReceivedAt = now
            });
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("Document library callback accepted DocumentFileId={DocumentFileId} CsitJobId={CsitJobId} KmJobId={KmJobId} Status={Status}",
                callback.DocumentFileId, callback.CsitJobId, callback.KmJobId, callback.Status);
            return NoContent();
        }
        catch (InvalidOperationException)
        {
            logger.LogWarning("Rejected document library callback transition DocumentFileId={DocumentFileId} CsitJobId={CsitJobId} KmJobId={KmJobId} Status={Status}",
                callback.DocumentFileId, callback.CsitJobId, callback.KmJobId, callback.Status);
            return Conflict(new { error = "invalid_transition" });
        }
    }

    private string ResolveDocumentPath(DocumentLibraryFile file)
    {
        var configuredRoot = file.StorageArea switch
        {
            DocumentStorageAreas.Staging => stagingOptions?.Value.RootPath?.Trim(),
            DocumentStorageAreas.Library => storageOptions?.Value.RootPath?.Trim(),
            _ => null
        };
        if (string.IsNullOrWhiteSpace(configuredRoot))
            throw new InvalidOperationException("Document storage root is unavailable.");
        var root = Path.GetFullPath(configuredRoot);
        var path = Path.GetFullPath(Path.Combine(root, file.RelativePath));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Document storage path is invalid.");
        return path;
    }

    private static bool IsValidDocumentLibraryCallback(DocumentLibraryStatusCallback? callback)
    {
        if (callback is null || callback.SchemaVersion != "1.0" || callback.CallbackId == Guid.Empty ||
            callback.SourceSystem != "CSIT" || callback.DocumentFileId == Guid.Empty ||
            callback.CsitJobId == Guid.Empty || string.IsNullOrWhiteSpace(callback.KmJobId) ||
            callback.Status is not ("Processing" or "Completed" or "Failed") ||
            callback.Timestamps is null || !UtcOrNull(callback.Timestamps.StartedAt) ||
            !UtcOrNull(callback.Timestamps.CompletedAt) || !UtcOrNull(callback.Timestamps.FailedAt))
            return false;

        if (callback.Status == "Processing")
            return callback.Timestamps.StartedAt.HasValue && !callback.Timestamps.CompletedAt.HasValue &&
                !callback.Timestamps.FailedAt.HasValue && callback.Result is null && callback.Error is null;
        if (callback.Status == "Failed")
            return callback.Timestamps.FailedAt.HasValue && !callback.Timestamps.CompletedAt.HasValue &&
                callback.Result is null && callback.Error is not null &&
                !string.IsNullOrWhiteSpace(callback.Error.Code) &&
                !string.IsNullOrWhiteSpace(callback.Error.Message) && callback.Error.Retryable.HasValue;
        return callback.Timestamps.CompletedAt.HasValue && !callback.Timestamps.FailedAt.HasValue &&
            callback.Error is null && IsValidCompletedResult(callback.Result);
    }

    private static bool IsValidEquipmentCallback(EquipmentDocumentStatusCallback? callback)
    {
        if (callback is null || callback.SchemaVersion != "1.0" ||
            string.IsNullOrWhiteSpace(callback.JobId) || callback.SourceSystem != "CSIT" ||
            callback.DocumentId == Guid.Empty || callback.Version != 1 ||
            callback.Status is not ("Processing" or "Completed" or "Failed") || callback.Timestamps is null)
            return false;
        if (callback.Status == "Processing")
            return callback.Timestamps.StartedAt.HasValue && callback.Result is null && callback.Error is null;
        if (callback.Status == "Failed")
            return callback.Timestamps.FailedAt.HasValue && callback.Result is null && callback.Error is not null &&
                !string.IsNullOrWhiteSpace(callback.Error.Code) && !string.IsNullOrWhiteSpace(callback.Error.Message);
        var result = callback.Result;
        return callback.Timestamps.CompletedAt.HasValue && callback.Error is null && result is not null &&
            result.MetadataSchemaVersion == "1.0" && result.TechnicalSpecifications is not null &&
            result.Limitations is not null && result.Evidence is not null &&
            result.TechnicalSpecifications.All(x => x is not null &&
                !string.IsNullOrWhiteSpace(x.Field) && !string.IsNullOrWhiteSpace(x.Value) &&
                x.Evidence is not null && !string.IsNullOrWhiteSpace(x.Evidence.SourceFileName)) &&
            result.Evidence.All(x => x is not null && !string.IsNullOrWhiteSpace(x.SourceFileName));
    }

    private static bool IsValidCompletedResult(DocumentLibraryCompletedResult? result)
    {
        if (result is null || result.MetadataSchemaVersion != "1.0" || result.Document is null ||
            result.Technical is null || result.Extraction is null || result.Indexing is null ||
            result.Warnings is null || !DocumentTypes.Contains(result.Document.DocumentType) ||
            result.Document.Authors is null || result.Document.Keywords is null || result.Document.Tags is null ||
            result.Technical.ProductNames is null || result.Technical.ModelNumbers is null ||
            result.Technical.VendorNames is null || result.Technical.Technologies is null ||
            result.Technical.Standards is null || result.Technical.SoftwareVersions is null ||
            result.Technical.HardwareVersions is null || result.Technical.ProjectNames is null ||
            result.Technical.TestItems is null || result.Technical.Measurements is null ||
            result.Technical.IssueKeys is null || result.Technical.ErrorCodes is null ||
            !ExtractionMethods.Contains(result.Extraction.Method) || result.Extraction.SheetNames is null ||
            result.Extraction.ExtractedCharacterCount < 0 || result.Extraction.PageCount < 0 ||
            result.Indexing.Status != "Completed" || string.IsNullOrWhiteSpace(result.Indexing.IndexId) ||
            result.Indexing.ChunkCount < 0 || result.Indexing.IndexedAt.Offset != TimeSpan.Zero ||
            result.Technical.Measurements.Any(x => x is null || string.IsNullOrWhiteSpace(x.Metric) ||
                string.IsNullOrWhiteSpace(x.Value)))
            return false;
        return result.Document.DocumentDate is null ||
            DateOnly.TryParseExact(result.Document.DocumentDate, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    private static bool UtcOrNull(DateTimeOffset? value) => !value.HasValue || value.Value.Offset == TimeSpan.Zero;

    private IActionResult OpenFile(string path, string? contentType, string fileName)
    {
        if (!System.IO.File.Exists(path)) return NotFound();
        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return File(stream, contentType ?? "application/octet-stream", Path.GetFileName(fileName));
        }
        catch (FileNotFoundException) { return NotFound(); }
        catch (DirectoryNotFoundException) { return NotFound(); }
    }
}
