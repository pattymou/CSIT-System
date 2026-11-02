using System.Data;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Config;
using SIT.DepartmentSystem.Web.Services.Approval;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public sealed record DocumentLibraryFileDeletionResult(
    Guid RequestId,
    Guid FileId,
    string Status,
    string StatusDisplay,
    string? ErrorCode,
    string? ErrorMessage,
    bool Retryable,
    bool Completed);

public sealed class DocumentLibraryFileDeletionService(
    IDbContextFactory<AppDbContext> dbFactory,
    IOptions<DocumentStorageOptions> storageOptions,
    IOptions<DocumentStagingOptions> stagingOptions,
    IOptions<KmDeletionOptions> deletionOptions,
    ILogger<DocumentLibraryFileDeletionService>? logger = null,
    IApprovalStagingStorage? approvalStaging = null)
{
    public async Task<DocumentLibraryFileDeletionResult?> RequestAsync(
        Guid fileId,
        string actor,
        CancellationToken cancellationToken = default)
    {
        if (fileId == Guid.Empty) return null;
        actor = string.IsNullOrWhiteSpace(actor) ? "SYSTEM" : actor.Trim();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            : null;

        var existing = await LockDeletionByFileAsync(db, fileId, cancellationToken);
        if (existing is not null)
        {
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            await TryAdvanceAsync(existing.Id, cancellationToken);
            return await GetAsync(existing.Id, cancellationToken);
        }

        var file = await LockFileAsync(db, fileId, cancellationToken);
        if (file is null) return null;
        existing = await LockDeletionByFileAsync(db, fileId, cancellationToken);
        if (existing is not null)
        {
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return await GetAsync(existing.Id, cancellationToken);
        }

        await EnsureUnreferencedAsync(db, file, cancellationToken);
        var job = DocumentLibraryFileDeletionJob.Create(file, actor, DateTime.UtcNow);
        db.DocumentLibraryFileDeletionJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);

        await TryAdvanceAsync(job.Id, cancellationToken);
        return await GetAsync(job.Id, cancellationToken);
    }

    public async Task<DocumentLibraryFileDeletionResult?> GetAsync(Guid requestId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var job = await db.DocumentLibraryFileDeletionJobs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == requestId, cancellationToken);
        return job is null ? null : ToResult(job);
    }

    public async Task<int> ProcessPendingAsync(int maxCount = 20,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var ids = await db.DocumentLibraryFileDeletionJobs.AsNoTracking()
            .Where(x => x.Status == DocumentLibraryFileDeletionStatuses.Preparing ||
                        x.Status == DocumentLibraryFileDeletionStatuses.KmVerified ||
                        x.Status == DocumentLibraryFileDeletionStatuses.LocalCleanup ||
                        x.Status == DocumentLibraryFileDeletionStatuses.CleanupFailed)
            .OrderBy(x => x.UpdatedAt).ThenBy(x => x.Id)
            .Select(x => x.Id).Take(maxCount).ToListAsync(cancellationToken);
        var advanced = 0;
        foreach (var id in ids)
            if (await TryAdvanceAsync(id, cancellationToken)) advanced++;
        return advanced;
    }

    public async Task<bool> TryAdvanceAsync(Guid requestId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            : null;
        var job = await LockDeletionAsync(db, requestId, cancellationToken);
        if (job is null || job.Status == DocumentLibraryFileDeletionStatuses.Completed)
            return job is not null;
        if (job.Status is DocumentLibraryFileDeletionStatuses.LocalCleanup or
            DocumentLibraryFileDeletionStatuses.KmVerified or
            DocumentLibraryFileDeletionStatuses.CleanupFailed)
        {
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return await TryCompleteLocalCleanupAsync(requestId, cancellationToken);
        }
        if (job.Status != DocumentLibraryFileDeletionStatuses.Preparing) return false;

        var file = await LockFileAsync(db, job.DocumentFileId, cancellationToken);
        if (file is null)
        {
            job.WaitForPreparation("DOCUMENT_RECORD_MISSING",
                "The formal document record is unavailable; cleanup remains blocked for investigation.",
                DateTime.UtcNow);
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return false;
        }
        EnsureSnapshot(job, file);
        await EnsureUnreferencedAsync(db, file, cancellationToken);

        var processing = await LockProcessingJobAsync(db, file.Id, cancellationToken);
        var now = DateTime.UtcNow;
        if (processing?.ArchiveStatus == DocumentArchiveStatuses.Archiving &&
            processing.ArchiveLeaseExpiresAt > now)
        {
            job.WaitForPreparation("ARCHIVE_IN_PROGRESS",
                "NAS archiving is finishing; deletion is held until the active lease is released.", now);
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return false;
        }
        if (processing?.ArchiveStatus == DocumentArchiveStatuses.Archiving &&
            processing.ArchiveLeaseToken is { } archiveLease)
            processing.ArchiveFailed(archiveLease, "Deletion requested; archive retry cancelled.", null, now);

        var outbox = await LockOutboxAsync(db, file.Id, cancellationToken);
        if (outbox?.Status == KmIntegrationStatus.Dispatching && outbox.LeaseExpiresAt > now)
        {
            job.WaitForPreparation("KM_INGESTION_IN_PROGRESS",
                "KM ingestion is finishing; deletion is held until the active lease is released.", now);
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return false;
        }
        if (outbox?.Status == KmIntegrationStatus.Dispatching && outbox.LeaseToken is { } outboxLease)
            outbox.DeliveryFailed(outboxLease, "CANCELLED_BY_DELETION",
                "Document deletion took ownership after the ingestion lease expired.", null, now);

        var neverDispatched = CanProveNeverDispatched(processing, outbox);
        if (neverDispatched)
        {
            if (outbox is not null) db.KmIntegrationOutbox.Remove(outbox);
            job.AllowLocalCleanup(now);
        }
        else
            job.RequireKmDeletion(deletionOptions.Value.Enabled, now);

        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return neverDispatched && await TryCompleteLocalCleanupAsync(requestId, cancellationToken);
    }

    public async Task<bool> TryCompleteLocalCleanupAsync(Guid requestId,
        CancellationToken cancellationToken = default)
    {
        var moved = new List<(string Source, string Quarantine)>();
        var committed = false;
        try
        {
            await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
            await using (var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                : null)
            {
                var job = await LockDeletionAsync(db, requestId, cancellationToken);
                if (job is null || job.Status == DocumentLibraryFileDeletionStatuses.Completed)
                    return job is not null;
                if (job.Status is not (DocumentLibraryFileDeletionStatuses.LocalCleanup or
                    DocumentLibraryFileDeletionStatuses.KmVerified or
                    DocumentLibraryFileDeletionStatuses.CleanupFailed)) return false;
                job.BeginLocalCleanup(DateTime.UtcNow);

                if (job.DbRecordsDeletedAt is null)
                {
                    var file = await LockFileAsync(db, job.DocumentFileId, cancellationToken)
                        ?? throw new InvalidOperationException(
                            "The formal document record disappeared before audited cleanup was committed.");
                    EnsureSnapshot(job, file);
                    await EnsureUnreferencedAsync(db, file, cancellationToken);

                    foreach (var path in ResolveManagedPaths(job).Distinct(StringComparer.OrdinalIgnoreCase))
                        await MoveVerifiedToQuarantineAsync(path, job, moved, cancellationToken);

                    var processing = await db.DocumentProcessingJobs
                        .SingleOrDefaultAsync(x => x.DocumentFileId == job.DocumentFileId,
                            cancellationToken);
                    var outboxes = await db.KmIntegrationOutbox.Where(x =>
                        x.EventType == KmIntegrationEventTypes.DocumentLibraryFileUploaded &&
                        x.SourceEntityType == KmIntegrationSourceEntityTypes.DocumentLibraryFile &&
                        x.DocumentId == job.DocumentFileId).ToListAsync(cancellationToken);
                    db.KmIntegrationOutbox.RemoveRange(outboxes);
                    if (processing is not null) db.DocumentProcessingJobs.Remove(processing);
                    db.DocumentLibraryFiles.Remove(file);
                    job.MarkDatabaseCleanupCompleted(DateTime.UtcNow);
                    await db.SaveChangesAsync(cancellationToken);
                }
                if (transaction is not null) await transaction.CommitAsync(cancellationToken);
                committed = true;
            }

            await DeleteQuarantinesAsync(requestId, cancellationToken);
            await DeleteArchiveTemporaryCopiesAsync(requestId, cancellationToken);
            await DeleteApprovalStagingCopyAsync(requestId, cancellationToken);
            await using var completeDb = await dbFactory.CreateDbContextAsync(cancellationToken);
            await using var completeTransaction = completeDb.Database.IsRelational()
                ? await completeDb.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                : null;
            var complete = await LockDeletionAsync(completeDb, requestId, cancellationToken);
            if (complete is null || complete.Status == DocumentLibraryFileDeletionStatuses.Completed)
                return complete is not null;
            complete.BeginLocalCleanup(DateTime.UtcNow);
            complete.MarkCleanupCompleted(DateTime.UtcNow);
            await completeDb.SaveChangesAsync(cancellationToken);
            if (completeTransaction is not null)
                await completeTransaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   InvalidOperationException or DbUpdateException)
        {
            if (!committed)
            {
                foreach (var item in moved.AsEnumerable().Reverse())
                {
                    try
                    {
                        if (File.Exists(item.Quarantine) && !File.Exists(item.Source))
                            File.Move(item.Quarantine, item.Source);
                    }
                    catch (Exception restoreError) when (restoreError is IOException or UnauthorizedAccessException)
                    {
                        logger?.LogCritical(restoreError,
                            "Document deletion rollback could not restore quarantine DeletionRequestId={DeletionRequestId} SourcePath={SourcePath}",
                            requestId, item.Source);
                    }
                }
            }
            logger?.LogError(ex,
                "Document library file cleanup failed DeletionRequestId={DeletionRequestId}", requestId);
            await MarkCleanupFailedAsync(requestId, ex, cancellationToken);
            return false;
        }
    }

    private async Task DeleteQuarantinesAsync(Guid requestId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var job = await db.DocumentLibraryFileDeletionJobs.AsNoTracking()
            .SingleAsync(x => x.Id == requestId, cancellationToken);
        foreach (var source in ResolveManagedPaths(job).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var quarantine = QuarantinePath(source, job.Id);
            if (!File.Exists(quarantine)) continue;
            await VerifyFileAsync(quarantine, job.FileSize, job.Sha256, cancellationToken);
            File.Delete(quarantine);
        }
    }

    private async Task DeleteArchiveTemporaryCopiesAsync(Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var job = await db.DocumentLibraryFileDeletionJobs.AsNoTracking()
            .SingleAsync(x => x.Id == requestId, cancellationToken);
        var nasPath = ResolvePath(storageOptions.Value.RootPath, job.RelativePath);
        var directory = Path.GetDirectoryName(nasPath)!;
        if (!Directory.Exists(directory)) return;
        var prefix = Path.GetFileName(nasPath) + ".archive-";
        foreach (var path in Directory.EnumerateFiles(directory, prefix + "*.tmp",
                     SearchOption.TopDirectoryOnly))
        {
            if (!Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            await VerifyFileAsync(path, job.FileSize, job.Sha256, cancellationToken);
            File.Delete(path);
        }
    }

    private async Task DeleteApprovalStagingCopyAsync(Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var job = await db.DocumentLibraryFileDeletionJobs.AsNoTracking()
            .SingleAsync(x => x.Id == requestId, cancellationToken);
        var submission = await db.PendingSubmissions.SingleOrDefaultAsync(x =>
            x.Id == job.DocumentFileId &&
            x.SourceModuleCode == ApprovalSourceMenuBinding.DocumentLibraryModule,
            cancellationToken);
        if (submission?.StagingStorageKey is null) return;
        if (approvalStaging is null)
            throw new InvalidOperationException(
                "Approval staging storage is unavailable; the deletion remains retryable.");
        await approvalStaging.DeleteAsync(submission.Id, submission.StagingStorageKey,
            cancellationToken);
        submission.ClearCommittedStagingFile(DateTime.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkCleanupFailedAsync(Guid requestId, Exception error,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var job = await db.DocumentLibraryFileDeletionJobs.SingleOrDefaultAsync(
                x => x.Id == requestId, cancellationToken);
            if (job is null || job.Status == DocumentLibraryFileDeletionStatuses.Completed) return;
            if (job.Status != DocumentLibraryFileDeletionStatuses.LocalCleanup)
                job.BeginLocalCleanup(DateTime.UtcNow);
            var code = error switch
            {
                UnauthorizedAccessException => "FILE_ACCESS_DENIED",
                IOException => "FILE_IO_FAILED",
                DbUpdateException => "LOCAL_DB_CLEANUP_FAILED",
                _ => "SAFETY_VALIDATION_FAILED"
            };
            job.MarkCleanupFailed(code, error.Message, DateTime.UtcNow);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception persistError)
        {
            logger?.LogCritical(persistError,
                "Document deletion failure state could not be persisted DeletionRequestId={DeletionRequestId}",
                requestId);
        }
    }

    private async Task MoveVerifiedToQuarantineAsync(string source,
        DocumentLibraryFileDeletionJob job,
        List<(string Source, string Quarantine)> moved,
        CancellationToken cancellationToken)
    {
        var quarantine = QuarantinePath(source, job.Id);
        if (File.Exists(source) && File.Exists(quarantine))
            throw new InvalidOperationException("Both the live file and its deletion quarantine exist.");
        if (File.Exists(source))
        {
            await VerifyFileAsync(source, job.FileSize, job.Sha256, cancellationToken);
            File.Move(source, quarantine, overwrite: false);
            moved.Add((source, quarantine));
        }
        else if (File.Exists(quarantine))
            await VerifyFileAsync(quarantine, job.FileSize, job.Sha256, cancellationToken);
    }

    private IEnumerable<string> ResolveManagedPaths(DocumentLibraryFileDeletionJob job)
    {
        yield return ResolvePath(storageOptions.Value.RootPath, job.RelativePath);
        yield return ResolvePath(stagingOptions.Value.RootPath, job.RelativePath);
    }

    private static string ResolvePath(string? configuredRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(configuredRoot))
            throw new InvalidOperationException("Document storage root is unavailable.");
        var root = Path.GetFullPath(configuredRoot);
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Document deletion path is outside its trusted root.");
        return path;
    }

    private static string QuarantinePath(string source, Guid requestId) =>
        source + ".csit-delete-" + requestId.ToString("N");

    private static async Task VerifyFileAsync(string path, long expectedSize, string expectedSha256,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length != expectedSize)
            throw new InvalidOperationException("Physical file size does not match the deletion snapshot.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, useAsync: true);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
        if (!string.Equals(hash, expectedSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("Physical file SHA256 does not match the deletion snapshot.");
    }

    private static bool CanProveNeverDispatched(DocumentProcessingJob? processing,
        KmIntegrationOutbox? outbox)
    {
        if (outbox is not null) return EquipmentFileDeletionService.CanProveNeverDispatched(outbox);
        return processing is not null && processing.Status == DocumentProcessingStatuses.Pending &&
            processing.AttemptCount == 0 && processing.LastAttemptAt is null &&
            processing.KmStartedAt is null && processing.KmCompletedAt is null &&
            processing.KmFailedAt is null && string.IsNullOrWhiteSpace(processing.MetadataJson);
    }

    private static void EnsureSnapshot(DocumentLibraryFileDeletionJob job, DocumentLibraryFile file)
    {
        if (file.Id != job.DocumentFileId || file.FolderId != job.FolderId ||
            file.FileSize != job.FileSize || file.RelativePath != job.RelativePath ||
            file.StorageArea != job.StorageArea || file.Sha256 != job.Sha256 ||
            file.OriginalFileName != job.FileName)
            throw new InvalidOperationException(
                "The document record no longer matches the immutable deletion snapshot.");
    }

    private static async Task EnsureUnreferencedAsync(AppDbContext db, DocumentLibraryFile file,
        CancellationToken cancellationToken)
    {
        if (await db.VerificationApplicationFiles.AsNoTracking().AnyAsync(
                x => x.DocumentLibraryFileId == file.Id, cancellationToken))
            throw new InvalidOperationException(
                "This document is owned by a verification application. Delete it through the source lifecycle.");
        if (await db.DocumentLibraryFiles.AsNoTracking().AnyAsync(x =>
                x.Id != file.Id && x.RelativePath == file.RelativePath, cancellationToken))
            throw new InvalidOperationException(
                "The physical path is referenced by another document and cannot be deleted here.");
    }

    private static DocumentLibraryFileDeletionResult ToResult(DocumentLibraryFileDeletionJob job) => new(
        job.Id, job.DocumentFileId, job.Status, StatusDisplay(job.Status), job.LastErrorCode,
        job.LastErrorMessage, job.LastErrorRetryable == true,
        job.Status == DocumentLibraryFileDeletionStatuses.Completed);

    public static string StatusDisplay(string status) => status switch
    {
        DocumentLibraryFileDeletionStatuses.Preparing => "刪除準備中",
        DocumentLibraryFileDeletionStatuses.Pending => "等待 KM 刪除確認",
        DocumentLibraryFileDeletionStatuses.Dispatching => "正在送出 KM 刪除要求",
        DocumentLibraryFileDeletionStatuses.Accepted => "KM 已接受刪除要求",
        DocumentLibraryFileDeletionStatuses.Processing => "KM 刪除處理中",
        DocumentLibraryFileDeletionStatuses.Failed => "KM 刪除失敗",
        DocumentLibraryFileDeletionStatuses.KmVerified => "KM 已驗證清除",
        DocumentLibraryFileDeletionStatuses.LocalCleanup => "本地檔案清理中",
        DocumentLibraryFileDeletionStatuses.CleanupFailed => "本地清理失敗，等待重試",
        DocumentLibraryFileDeletionStatuses.Completed => "刪除完成",
        _ => "刪除狀態未知"
    };

    private static bool IsPostgres(AppDbContext db) =>
        db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;

    private static Task<DocumentLibraryFileDeletionJob?> LockDeletionAsync(AppDbContext db,
        Guid requestId, CancellationToken cancellationToken) => IsPostgres(db)
        ? db.DocumentLibraryFileDeletionJobs.FromSqlInterpolated($"""
            SELECT * FROM document_file_deletion_jobs WHERE id = {requestId} FOR UPDATE
            """).SingleOrDefaultAsync(cancellationToken)
        : db.DocumentLibraryFileDeletionJobs.SingleOrDefaultAsync(x => x.Id == requestId,
            cancellationToken);

    private static Task<DocumentLibraryFileDeletionJob?> LockDeletionByFileAsync(AppDbContext db,
        Guid fileId, CancellationToken cancellationToken) => IsPostgres(db)
        ? db.DocumentLibraryFileDeletionJobs.FromSqlInterpolated($"""
            SELECT * FROM document_file_deletion_jobs WHERE document_file_id = {fileId} FOR UPDATE
            """).SingleOrDefaultAsync(cancellationToken)
        : db.DocumentLibraryFileDeletionJobs.SingleOrDefaultAsync(x => x.DocumentFileId == fileId,
            cancellationToken);

    private static Task<DocumentLibraryFile?> LockFileAsync(AppDbContext db, Guid fileId,
        CancellationToken cancellationToken) => IsPostgres(db)
        ? db.DocumentLibraryFiles.FromSqlInterpolated($"""
            SELECT * FROM document_files WHERE id = {fileId} FOR UPDATE
            """).SingleOrDefaultAsync(cancellationToken)
        : db.DocumentLibraryFiles.SingleOrDefaultAsync(x => x.Id == fileId, cancellationToken);

    private static Task<DocumentProcessingJob?> LockProcessingJobAsync(AppDbContext db, Guid fileId,
        CancellationToken cancellationToken) => IsPostgres(db)
        ? db.DocumentProcessingJobs.FromSqlInterpolated($"""
            SELECT * FROM document_processing_jobs WHERE document_file_id = {fileId} FOR UPDATE
            """).SingleOrDefaultAsync(cancellationToken)
        : db.DocumentProcessingJobs.SingleOrDefaultAsync(x => x.DocumentFileId == fileId,
            cancellationToken);

    private static Task<KmIntegrationOutbox?> LockOutboxAsync(AppDbContext db, Guid fileId,
        CancellationToken cancellationToken) => IsPostgres(db)
        ? db.KmIntegrationOutbox.FromSqlInterpolated($"""
            SELECT * FROM km_integration_outbox
            WHERE event_type = 'DocumentLibraryFileUploaded'
              AND source_entity_type = 'DocumentLibraryFile'
              AND document_id = {fileId}
            FOR UPDATE
            """).SingleOrDefaultAsync(cancellationToken)
        : db.KmIntegrationOutbox.SingleOrDefaultAsync(x =>
            x.EventType == KmIntegrationEventTypes.DocumentLibraryFileUploaded &&
            x.SourceEntityType == KmIntegrationSourceEntityTypes.DocumentLibraryFile &&
            x.DocumentId == fileId, cancellationToken);
}
