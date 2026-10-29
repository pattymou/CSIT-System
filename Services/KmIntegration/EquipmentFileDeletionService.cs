using System.Data;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public sealed record EquipmentFileDeletionRequestResult(Guid RequestId, string Status,
    bool CompletedLocally);

public sealed class EquipmentFileDeletionService(
    AppDbContext db,
    ILogger<EquipmentFileDeletionService>? logger = null)
{
    public async Task<EquipmentFileDeletionRequestResult?> RequestAsync(Guid fileId, string actor,
        CancellationToken cancellationToken = default)
    {
        if (fileId == Guid.Empty) return null;
        actor = string.IsNullOrWhiteSpace(actor) ? "SYSTEM" : actor.Trim();
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken) : null;

        var file = IsPostgres(db)
            ? await db.ApparatusFiles.FromSqlInterpolated($"""
                SELECT * FROM apparatus_files WHERE "Id" = {fileId} FOR UPDATE
                """).SingleOrDefaultAsync(cancellationToken)
            : await db.ApparatusFiles.SingleOrDefaultAsync(x => x.Id == fileId, cancellationToken);
        if (file is null) return null;
        var apparatus = await db.Apparatuses.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == file.ApparatusId, cancellationToken);
        if (apparatus?.ModuleCode != "equipment")
            throw new InvalidOperationException("Equipment deletion lifecycle only accepts Equipment attachments.");

        var existing = await db.EquipmentFileDeletionJobs
            .SingleOrDefaultAsync(x => x.ApparatusFileId == fileId, cancellationToken);
        if (existing is not null)
        {
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            if (existing.Status is EquipmentFileDeletionStatuses.LocalCleanup or
                EquipmentFileDeletionStatuses.CleanupFailed or EquipmentFileDeletionStatuses.KmVerified)
                await TryCompleteLocalCleanupAsync(existing.Id, cancellationToken);
            var current = await db.EquipmentFileDeletionJobs.AsNoTracking()
                .SingleAsync(x => x.Id == existing.Id, cancellationToken);
            return new(current.Id, current.Status,
                current.Status == EquipmentFileDeletionStatuses.Completed);
        }

        var outbox = IsPostgres(db)
            ? await db.KmIntegrationOutbox.FromSqlInterpolated($"""
                SELECT * FROM km_integration_outbox
                WHERE event_type = 'EquipmentDocumentArchived'
                  AND source_entity_type = 'ApparatusFile'
                  AND source_entity_id = {fileId}
                FOR UPDATE
                """).SingleOrDefaultAsync(cancellationToken)
            : await db.KmIntegrationOutbox.SingleOrDefaultAsync(x =>
                x.EventType == KmIntegrationEventTypes.EquipmentDocumentArchived &&
                x.SourceEntityType == KmIntegrationSourceEntityTypes.ApparatusFile &&
                x.SourceEntityId == fileId, cancellationToken);

        var requiresKmDeletion = outbox is not null && !CanProveNeverDispatched(outbox);
        var now = DateTime.UtcNow;
        var job = EquipmentFileDeletionJob.Create(file, actor, requiresKmDeletion, now);
        db.EquipmentFileDeletionJobs.Add(job);
        if (!requiresKmDeletion && outbox is not null)
            db.KmIntegrationOutbox.Remove(outbox);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);

        if (!requiresKmDeletion)
            await TryCompleteLocalCleanupAsync(job.Id, cancellationToken);
        var result = await db.EquipmentFileDeletionJobs.AsNoTracking()
            .SingleAsync(x => x.Id == job.Id, cancellationToken);
        return new(result.Id, result.Status, result.Status == EquipmentFileDeletionStatuses.Completed);
    }

    public async Task<bool> TryCompleteLocalCleanupAsync(Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken) : null;
        var job = IsPostgres(db)
            ? await db.EquipmentFileDeletionJobs.FromSqlInterpolated($"""
                SELECT * FROM equipment_file_deletion_jobs WHERE id = {jobId} FOR UPDATE
                """).SingleOrDefaultAsync(cancellationToken)
            : await db.EquipmentFileDeletionJobs.SingleOrDefaultAsync(x => x.Id == jobId,
                cancellationToken);
        if (job is null || job.Status == EquipmentFileDeletionStatuses.Completed) return job is not null;
        if (job.Status is not (EquipmentFileDeletionStatuses.LocalCleanup or
            EquipmentFileDeletionStatuses.KmVerified or EquipmentFileDeletionStatuses.CleanupFailed))
            return false;

        var file = IsPostgres(db)
            ? await db.ApparatusFiles.FromSqlInterpolated($"""
                SELECT * FROM apparatus_files WHERE "Id" = {job.ApparatusFileId} FOR UPDATE
                """).SingleOrDefaultAsync(cancellationToken)
            : await db.ApparatusFiles.SingleOrDefaultAsync(x => x.Id == job.ApparatusFileId,
                cancellationToken);
        if (file is null)
        {
            job.BeginLocalCleanup(DateTime.UtcNow);
            job.MarkLocalCleanupCompleted(false, DateTime.UtcNow);
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return true;
        }

        var fullPath = Path.Combine(file.FilePath, file.FileName);
        var quarantinePath = fullPath + ".csit-delete-" + job.Id.ToString("N");
        var moved = false;
        var transactionCompleted = false;
        var operation = "BeginLocalCleanup";
        try
        {
            job.BeginLocalCleanup(DateTime.UtcNow);
            if (File.Exists(fullPath))
            {
                operation = "DeleteExistingQuarantine";
                if (File.Exists(quarantinePath)) File.Delete(quarantinePath);
                operation = "MoveSourceToQuarantine";
                File.Move(fullPath, quarantinePath);
                moved = true;
            }
            else if (File.Exists(quarantinePath)) moved = true;

            operation = "PersistLocalCleanup";
            var outboxes = await db.KmIntegrationOutbox.Where(x =>
                x.EventType == KmIntegrationEventTypes.EquipmentDocumentArchived &&
                x.SourceEntityType == KmIntegrationSourceEntityTypes.ApparatusFile &&
                x.SourceEntityId == file.Id).ToListAsync(cancellationToken);
            db.KmIntegrationOutbox.RemoveRange(outboxes);
            db.ApparatusFiles.Remove(file);
            job.MarkLocalCleanupCompleted(moved || !File.Exists(fullPath), DateTime.UtcNow);
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            transactionCompleted = true;
            operation = "DeleteQuarantine";
            if (File.Exists(quarantinePath)) File.Delete(quarantinePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DbUpdateException)
        {
            logger?.LogError(ex,
                "Equipment file local cleanup failed Operation={Operation} EquipmentId={EquipmentId} ApparatusFileId={ApparatusFileId} DeletionJobId={DeletionJobId} SourcePath={SourcePath} QuarantinePath={QuarantinePath}",
                operation, job.EquipmentId, job.ApparatusFileId, job.Id, fullPath, quarantinePath);
            if (!transactionCompleted && transaction is not null)
                await transaction.RollbackAsync(CancellationToken.None);
            if (!transactionCompleted && moved && File.Exists(quarantinePath) && !File.Exists(fullPath))
            {
                try { File.Move(quarantinePath, fullPath); }
                catch (Exception restoreEx) when (restoreEx is IOException or UnauthorizedAccessException)
                {
                    logger?.LogCritical(restoreEx,
                        "Equipment file cleanup rollback could not restore quarantine EquipmentId={EquipmentId} ApparatusFileId={ApparatusFileId} DeletionJobId={DeletionJobId} SourcePath={SourcePath} QuarantinePath={QuarantinePath}",
                        job.EquipmentId, job.ApparatusFileId, job.Id, fullPath, quarantinePath);
                }
            }
            if (transactionCompleted)
            {
                // The source has already been removed from its live path and the database commit
                // succeeded. Do not move a completed job backwards if final quarantine disposal
                // is denied; retain the server-side path in the structured log for operations.
                return true;
            }
            db.ChangeTracker.Clear();
            var failed = await db.EquipmentFileDeletionJobs.SingleAsync(x => x.Id == jobId,
                cancellationToken);
            failed.BeginLocalCleanup(DateTime.UtcNow);
            failed.MarkCleanupFailed(
                ex is UnauthorizedAccessException ? "FILE_ACCESS_DENIED" :
                ex is IOException ? "FILE_IO_FAILED" : "LOCAL_DB_CLEANUP_FAILED",
                ex is UnauthorizedAccessException ? "檔案存取遭拒。" :
                ex is IOException ? "本地檔案清理失敗，等待安全重試。" :
                "本地清理資料更新失敗，等待安全重試。",
                DateTime.UtcNow);
            await db.SaveChangesAsync(cancellationToken);
            return false;
        }
    }

    public static bool CanProveNeverDispatched(KmIntegrationOutbox outbox) =>
        outbox.Status == KmIntegrationStatus.Pending && outbox.RetryCount == 0 &&
        outbox.LastAttemptAt is null && outbox.LeaseToken is null &&
        outbox.LeaseExpiresAt is null && outbox.KmJobId is null &&
        outbox.SentAt is null && outbox.CompletedAt is null;

    private static bool IsPostgres(AppDbContext context) =>
        context.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
}
