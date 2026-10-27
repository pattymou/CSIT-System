using System.Data;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Config;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public sealed class DocumentLibraryArchiveWorker(
    IDbContextFactory<AppDbContext> dbFactory,
    IOptions<KmIntegrationOptions> configured,
    IOptions<DocumentStorageOptions> storageOptions,
    IOptions<DocumentStagingOptions> stagingOptions,
    ILogger<DocumentLibraryArchiveWorker> logger)
{
    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken = default)
    {
        var options = configured.Value;
        if (!options.Enabled) return 0;
        Validate(options);
        await CleanupCompletedStagingCopiesAsync(cancellationToken);
        var count = 0;
        for (var i = 0; i < 20; i++)
        {
            var claim = await ClaimNextAsync(options, cancellationToken);
            if (claim is null) break;
            await ArchiveAsync(claim.Value.JobId, claim.Value.Lease, options, cancellationToken);
            count++;
        }
        return count;
    }

    private async Task<(Guid JobId, Guid Lease)?> ClaimNextAsync(
        KmIntegrationOptions options,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken) : null;
        var now = DateTime.UtcNow;
        DocumentProcessingJob? job;
        if (db.Database.IsRelational())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE document_processing_jobs
                SET archive_status = 'Failed', archive_lease_token = NULL,
                    archive_lease_expires_at = NULL, archive_next_retry_at = NULL,
                    archive_last_error = 'NAS archive retry limit reached.', updated_at = {now}
                WHERE status = 'Completed' AND archive_status = 'Archiving'
                  AND archive_lease_expires_at <= {now}
                  AND archive_attempt_count >= {options.ArchiveMaxRetryCount}
                """, cancellationToken);
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.Transaction = transaction!.GetDbTransaction();
            command.CommandText = """
                SELECT job.id
                FROM document_processing_jobs AS job
                JOIN document_files AS file ON file.id = job.document_file_id
                WHERE job.status = 'Completed' AND file.storage_area = 'Staging'
                  AND job.archive_attempt_count < @max
                  AND (job.archive_status = 'Pending'
                    OR (job.archive_status = 'Failed' AND job.archive_next_retry_at <= @now)
                    OR (job.archive_status = 'Archiving' AND job.archive_lease_expires_at <= @now))
                ORDER BY job.updated_at, job.id
                LIMIT 1 FOR UPDATE OF job SKIP LOCKED
                """;
            var max = command.CreateParameter(); max.ParameterName = "max";
            max.Value = options.ArchiveMaxRetryCount;
            var at = command.CreateParameter(); at.ParameterName = "now"; at.Value = now;
            command.Parameters.Add(max); command.Parameters.Add(at);
            var id = await command.ExecuteScalarAsync(cancellationToken);
            job = id is Guid value
                ? await db.DocumentProcessingJobs.SingleAsync(x => x.Id == value, cancellationToken)
                : null;
        }
        else
        {
            job = await db.DocumentProcessingJobs.Include(x => x.DocumentFile)
                .OrderBy(x => x.UpdatedAt).ThenBy(x => x.Id)
                .FirstOrDefaultAsync(x => x.Status == DocumentProcessingStatuses.Completed &&
                    x.DocumentFile.StorageArea == DocumentStorageAreas.Staging &&
                    x.ArchiveAttemptCount < options.ArchiveMaxRetryCount &&
                    (x.ArchiveStatus == DocumentArchiveStatuses.Pending ||
                     (x.ArchiveStatus == DocumentArchiveStatuses.Failed && x.ArchiveNextRetryAt <= now) ||
                     (x.ArchiveStatus == DocumentArchiveStatuses.Archiving && x.ArchiveLeaseExpiresAt <= now)),
                    cancellationToken);
        }
        if (job is null) return null;
        var lease = Guid.NewGuid();
        job.ClaimArchive(lease, now, TimeSpan.FromSeconds(options.ArchiveLeaseSeconds));
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Document NAS archive claimed CsitJobId={CsitJobId} DocumentFileId={DocumentFileId} Attempt={Attempt}",
            job.Id, job.DocumentFileId, job.ArchiveAttemptCount);
        return (job.Id, lease);
    }

    private async Task ArchiveAsync(Guid jobId, Guid lease, KmIntegrationOptions options,
        CancellationToken cancellationToken)
    {
        string? temporaryPath = null;
        try
        {
            DocumentProcessingJob job;
            await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
                job = await db.DocumentProcessingJobs.AsNoTracking().Include(x => x.DocumentFile)
                    .SingleAsync(x => x.Id == jobId, cancellationToken);
            var file = job.DocumentFile;
            var sourcePath = ResolvePath(stagingOptions.Value.RootPath, file.RelativePath);
            var targetPath = ResolvePath(storageOptions.Value.RootPath, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            temporaryPath = targetPath + $".archive-{lease:N}.tmp";

            string sha256;
            if (File.Exists(targetPath))
            {
                sha256 = await HashAsync(targetPath, cancellationToken);
                if (!string.Equals(sha256, file.Sha256, StringComparison.Ordinal))
                    throw new InvalidOperationException("NAS target exists with a different checksum.");
            }
            else
            {
                if (!File.Exists(sourcePath))
                    throw new FileNotFoundException("The staged document is unavailable.");
                var copied = await CopyAndHashAsync(sourcePath, temporaryPath, cancellationToken);
                if (copied.Size != file.FileSize || !string.Equals(copied.Sha256, file.Sha256,
                        StringComparison.Ordinal))
                    throw new InvalidOperationException("Staging file size or checksum does not match its database record.");
                File.Move(temporaryPath, targetPath, overwrite: false);
                temporaryPath = null;
                sha256 = copied.Sha256;
            }

            await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
            await using (var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken) : null)
            {
                var current = db.Database.IsRelational()
                    ? await db.DocumentProcessingJobs
                        .FromSqlInterpolated($"SELECT * FROM document_processing_jobs WHERE id = {jobId} FOR UPDATE")
                        .Include(x => x.DocumentFile).SingleAsync(cancellationToken)
                    : await db.DocumentProcessingJobs.Include(x => x.DocumentFile)
                        .SingleAsync(x => x.Id == jobId, cancellationToken);
                if (current.ArchiveLeaseToken != lease ||
                    current.ArchiveStatus != DocumentArchiveStatuses.Archiving)
                    return;
                current.DocumentFile.StorageArea = DocumentStorageAreas.Library;
                current.ArchiveCompleted(lease, sha256, DateTime.UtcNow);
                await db.SaveChangesAsync(cancellationToken);
                if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            }

            TryDeleteVerifiedStagingCopy(sourcePath, targetPath, sha256, file.Id);
            logger.LogInformation("Document NAS archive completed CsitJobId={CsitJobId} DocumentFileId={DocumentFileId} Sha256={Sha256}",
                job.Id, file.Id, sha256);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (temporaryPath is not null && File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); }
                catch { /* A lease retry uses a different exact temporary name. */ }
            }
            await RecordFailureAsync(jobId, lease, options, ex.Message, cancellationToken);
            logger.LogWarning("Document NAS archive failed CsitJobId={CsitJobId} ErrorType={ErrorType}",
                jobId, ex.GetType().Name);
        }
    }

    private async Task RecordFailureAsync(Guid jobId, Guid lease, KmIntegrationOptions options,
        string message, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken) : null;
        var job = db.Database.IsRelational()
            ? await db.DocumentProcessingJobs
                .FromSqlInterpolated($"SELECT * FROM document_processing_jobs WHERE id = {jobId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
            : await db.DocumentProcessingJobs.SingleOrDefaultAsync(x => x.Id == jobId, cancellationToken);
        if (job is null || job.ArchiveLeaseToken != lease ||
            job.ArchiveStatus != DocumentArchiveStatuses.Archiving) return;
        var now = DateTime.UtcNow;
        var schedule = options.ArchiveRetryScheduleSeconds;
        var next = job.ArchiveAttemptCount < options.ArchiveMaxRetryCount
            ? now.AddSeconds(schedule[Math.Min(job.ArchiveAttemptCount - 1, schedule.Length - 1)])
            : (DateTime?)null;
        job.ArchiveFailed(lease, message, next, now);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
    }

    private async Task CleanupCompletedStagingCopiesAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var completed = await db.DocumentProcessingJobs.AsNoTracking().Include(x => x.DocumentFile)
            .Where(x => x.ArchiveStatus == DocumentArchiveStatuses.Completed &&
                x.DocumentFile.StorageArea == DocumentStorageAreas.Library)
            .OrderBy(x => x.ArchivedAt).Take(100).ToListAsync(cancellationToken);
        foreach (var job in completed)
        {
            var source = ResolvePath(stagingOptions.Value.RootPath, job.DocumentFile.RelativePath);
            var target = ResolvePath(storageOptions.Value.RootPath, job.DocumentFile.RelativePath);
            if (!File.Exists(source) || !File.Exists(target) ||
                string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) continue;
            var targetHash = await HashAsync(target, cancellationToken);
            if (string.Equals(targetHash, job.ArchiveSha256, StringComparison.Ordinal))
                TryDeleteVerifiedStagingCopy(source, target, targetHash, job.DocumentFileId);
        }
    }

    private void TryDeleteVerifiedStagingCopy(string source, string target, string sha256, Guid documentFileId)
    {
        if (!File.Exists(source) || string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) return;
        try { File.Delete(source); }
        catch (Exception ex)
        {
            logger.LogWarning("Staging cleanup deferred after verified NAS archive DocumentFileId={DocumentFileId} Sha256={Sha256} ErrorType={ErrorType}",
                documentFileId, sha256, ex.GetType().Name);
        }
    }

    private static async Task<(long Size, string Sha256)> CopyAndHashAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, useAsync: true);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 81920, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long size = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            hash.AppendData(buffer, 0, read);
            size += read;
        }
        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
        return (size, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static string ResolvePath(string? configuredRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(configuredRoot))
            throw new InvalidOperationException("Document archive root is unavailable.");
        var root = Path.GetFullPath(configuredRoot);
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Document archive path is invalid.");
        return path;
    }

    private static void Validate(KmIntegrationOptions options)
    {
        if (options.ArchivePollSeconds < 1 || options.ArchiveLeaseSeconds < 5 ||
            options.ArchiveMaxRetryCount < 1 || options.ArchiveRetryScheduleSeconds.Length == 0 ||
            options.ArchiveRetryScheduleSeconds.Any(x => x < 1))
            throw new InvalidOperationException("Document archive retry or lease configuration is invalid.");
    }
}

public sealed class DocumentLibraryArchiveBackgroundService(
    IServiceScopeFactory scopes,
    IOptions<KmIntegrationOptions> configured,
    ILogger<DocumentLibraryArchiveBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(
            Math.Max(1, configured.Value.ArchivePollSeconds)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!configured.Value.Enabled) continue;
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<DocumentLibraryArchiveWorker>()
                    .ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Document library NAS archive worker failed."); }
        }
    }
}
