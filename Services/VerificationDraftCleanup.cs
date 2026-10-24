using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Config;

namespace SIT.DepartmentSystem.Web.Services;

public sealed record VerificationDraftCleanupReport(
    DateTime CutoffUtc,
    int EligibleDrafts,
    int EligibleAttachments,
    int DeletableFiles,
    int ProtectedFiles,
    int ExcludedRecentDrafts,
    int ExcludedLegacyDrafts,
    int ExcludedNonDraftApplications,
    int DeletedDrafts,
    int PendingRetries);

internal static class VerificationDraftCleanupQueries
{
    public static IQueryable<VerificationApplication> DynamicApplications(
        IQueryable<VerificationApplication> applications) =>
        applications.Where(x => x.ValidationTemplateVersionId.HasValue);

    public static IQueryable<VerificationApplication> LegacyDrafts(
        IQueryable<VerificationApplication> applications) =>
        applications.Where(x =>
            x.Status == VerificationApplicationStatus.Draft &&
            !x.ValidationTemplateVersionId.HasValue);
}

public interface IVerificationDraftFileSystem
{
    void DeleteStagingFile(string stagingRoot, string relativePath);
}

public sealed class VerificationDraftFileSystem : IVerificationDraftFileSystem
{
    public void DeleteStagingFile(string stagingRoot, string relativePath)
    {
        var absolutePath = VerificationDraftAttachmentSafety.ResolveStagingPath(stagingRoot, relativePath);
        if (File.Exists(absolutePath)) File.Delete(absolutePath);
        VerificationDraftAttachmentSafety.DeleteEmptyApplicationDirectory(stagingRoot, absolutePath);
    }
}

internal static class VerificationDraftAttachmentSafety
{
    public static async Task<IReadOnlyList<string>> GetExclusivePathsAsync(
        AppDbContext db,
        IEnumerable<VerificationApplicationFile> files,
        CancellationToken cancellationToken)
    {
        var candidates = files
            .Where(x => !x.DocumentLibraryFileId.HasValue && !string.IsNullOrWhiteSpace(x.FilePath))
            .Select(x => x.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidates.Count == 0) return [];

        var referenced = await db.DocumentLibraryFiles.AsNoTracking()
            .Where(x => x.StorageArea == DocumentStorageAreas.Staging && candidates.Contains(x.RelativePath))
            .Select(x => x.RelativePath)
            .ToListAsync(cancellationToken);
        return candidates.Except(referenced, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string ResolveStagingPath(string stagingRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(stagingRoot))
            throw new InvalidOperationException("尚未設定 DocumentStaging:RootPath。");
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidOperationException("附件儲存路徑無效。");
        var root = Path.GetFullPath(stagingRoot.Trim());
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("附件儲存路徑無效。");
        return fullPath;
    }

    public static void DeleteEmptyApplicationDirectory(string stagingRoot, string absolutePath)
    {
        var directory = Path.GetDirectoryName(absolutePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory) ||
            Directory.EnumerateFileSystemEntries(directory).Any()) return;
        var root = Path.GetFullPath(stagingRoot).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            Directory.Delete(directory);
    }
}

public sealed class VerificationDraftCleanupJournal(IOptions<VerificationDraftCleanupOptions> options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path = options.Value.StatePath?.Trim() ?? string.Empty;

    public async Task<IReadOnlyList<PendingVerificationDraftCleanup>> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await ReadUnsafeAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    public async Task UpsertAsync(PendingVerificationDraftCleanup entry, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var entries = await ReadUnsafeAsync(cancellationToken);
            entries.RemoveAll(x => x.ApplicationId == entry.ApplicationId);
            entries.Add(entry);
            await WriteUnsafeAsync(entries, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(Guid applicationId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var entries = await ReadUnsafeAsync(cancellationToken);
            if (entries.RemoveAll(x => x.ApplicationId == applicationId) > 0)
                await WriteUnsafeAsync(entries, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private async Task<List<PendingVerificationDraftCleanup>> ReadUnsafeAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_path) || !File.Exists(_path)) return [];
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<List<PendingVerificationDraftCleanup>>(
            stream, JsonOptions, cancellationToken) ?? [];
    }

    private async Task WriteUnsafeAsync(List<PendingVerificationDraftCleanup> entries, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_path))
            throw new InvalidOperationException("VerificationDraftCleanup:StatePath 尚未設定。");
        var fullPath = Path.GetFullPath(_path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = fullPath + ".tmp";
        await using (var stream = new FileStream(
                         temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            await JsonSerializer.SerializeAsync(stream, entries, JsonOptions, cancellationToken);
        File.Move(temporaryPath, fullPath, overwrite: true);
    }
}

public sealed class PendingVerificationDraftCleanup
{
    public Guid ApplicationId { get; set; }
    public DateTime QualifiedUpdatedAtUtc { get; set; }
    public List<string> RelativePaths { get; set; } = [];
    public bool DatabaseDeleted { get; set; }
    public int AttemptCount { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastAttemptAtUtc { get; set; }
    public string? LastError { get; set; }
}

public sealed class VerificationDraftCleanupWorker(
    IDbContextFactory<AppDbContext> dbFactory,
    IOptions<VerificationDraftCleanupOptions> cleanupOptions,
    IOptions<DocumentStagingOptions> stagingOptions,
    VerificationDraftCleanupJournal journal,
    IVerificationDraftFileSystem fileSystem,
    ILogger<VerificationDraftCleanupWorker> logger)
{
    private readonly VerificationDraftCleanupOptions _options = cleanupOptions.Value;
    private readonly string _stagingRoot = stagingOptions.Value.RootPath;

    public async Task<VerificationDraftCleanupReport> RunAsync(
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions();
        if (utcNow.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Cleanup time must be UTC.", nameof(utcNow));
        var cutoff = utcNow - TimeSpan.FromDays(_options.RetentionDays);
        var pendingBefore = (await journal.ReadAsync(cancellationToken)).Count;
        var report = await GetReportAsync(cutoff, pendingBefore, cancellationToken);

        if (_options.DryRun)
        {
            LogDryRun(report);
            return report;
        }
        if (!_options.DeletionConfirmed)
            throw new InvalidOperationException(
                "自動刪除尚未另行確認；請保持 DryRun=true，或明確設定 DeletionConfirmed=true。");

        await RetryPendingAsync(utcNow, cancellationToken);
        await using var candidateDb = await dbFactory.CreateDbContextAsync(cancellationToken);
        var candidateIds = await VerificationDraftCleanupQueries
            .DynamicApplications(candidateDb.VerificationApplications.AsNoTracking())
            .Where(x => x.Status == VerificationApplicationStatus.Draft &&
                        x.UpdatedAt <= cutoff)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        var deleted = 0;
        foreach (var applicationId in candidateIds)
            if (await DeleteEligibleDraftAsync(applicationId, cutoff, utcNow, cancellationToken)) deleted++;

        var pendingAfter = (await journal.ReadAsync(cancellationToken)).Count;
        logger.LogInformation(
            "VERIFICATION_DRAFT_CLEANUP_COMPLETE DeletedDrafts={DeletedDrafts} PendingRetries={PendingRetries} CutoffUtc={CutoffUtc}",
            deleted, pendingAfter, cutoff);
        return report with { DeletedDrafts = deleted, PendingRetries = pendingAfter };
    }

    private async Task<VerificationDraftCleanupReport> GetReportAsync(
        DateTime cutoff,
        int pendingRetries,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var dynamicApplications = VerificationDraftCleanupQueries
            .DynamicApplications(db.VerificationApplications.AsNoTracking());
        var eligibleIds = await dynamicApplications
            .Where(x => x.Status == VerificationApplicationStatus.Draft && x.UpdatedAt <= cutoff)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        var files = eligibleIds.Count == 0
            ? []
            : await db.VerificationApplicationFiles.AsNoTracking()
                .Where(x => eligibleIds.Contains(x.VerificationApplicationId))
                .ToListAsync(cancellationToken);
        var exclusive = await VerificationDraftAttachmentSafety.GetExclusivePathsAsync(db, files, cancellationToken);
        var allPaths = files.Where(x => !string.IsNullOrWhiteSpace(x.FilePath))
            .Select(x => x.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var recent = await dynamicApplications.CountAsync(
            x => x.Status == VerificationApplicationStatus.Draft && x.UpdatedAt > cutoff, cancellationToken);
        var legacy = await VerificationDraftCleanupQueries
            .LegacyDrafts(db.VerificationApplications.AsNoTracking())
            .CountAsync(cancellationToken);
        var nonDraft = await dynamicApplications.CountAsync(
            x => x.Status != VerificationApplicationStatus.Draft, cancellationToken);
        return new VerificationDraftCleanupReport(
            cutoff, eligibleIds.Count, files.Count, exclusive.Count, allPaths - exclusive.Count,
            recent, legacy, nonDraft, 0, pendingRetries);
    }

    private async Task<bool> DeleteEligibleDraftAsync(
        Guid applicationId,
        DateTime cutoff,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        IDbContextTransaction? transaction = null;
        PendingVerificationDraftCleanup? pending = null;
        try
        {
            if (db.Database.IsRelational())
                transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            var query = db.Database.IsRelational()
                ? db.VerificationApplications.FromSqlInterpolated(
                    $"SELECT * FROM verification_applications WHERE id = {applicationId} FOR UPDATE SKIP LOCKED")
                : db.VerificationApplications.AsQueryable();
            var application = await query.Include(x => x.Files)
                .SingleOrDefaultAsync(x => x.Id == applicationId, cancellationToken);
            if (application is null || application.Status != VerificationApplicationStatus.Draft ||
                !application.ValidationTemplateVersionId.HasValue || application.UpdatedAt > cutoff)
            {
                if (transaction is not null) await transaction.CommitAsync(cancellationToken);
                return false;
            }

            var paths = await VerificationDraftAttachmentSafety.GetExclusivePathsAsync(
                db, application.Files, cancellationToken);
            if (paths.Count > 0)
            {
                pending = new PendingVerificationDraftCleanup
                {
                    ApplicationId = application.Id,
                    QualifiedUpdatedAtUtc = application.UpdatedAt,
                    RelativePaths = paths.ToList(),
                    CreatedAtUtc = utcNow
                };
                await journal.UpsertAsync(pending, cancellationToken);
            }

            db.VerificationApplications.Remove(application);
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }

        if (pending is not null)
        {
            pending.DatabaseDeleted = true;
            await journal.UpsertAsync(pending, cancellationToken);
            await DeletePendingFilesAsync(pending, utcNow, cancellationToken);
        }
        logger.LogInformation("Verification draft cleanup deleted ApplicationId={ApplicationId}.", applicationId);
        return true;
    }

    private async Task RetryPendingAsync(DateTime utcNow, CancellationToken cancellationToken)
    {
        foreach (var pending in await journal.ReadAsync(cancellationToken))
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            if (await db.VerificationApplications.AsNoTracking()
                    .AnyAsync(x => x.Id == pending.ApplicationId, cancellationToken))
            {
                await journal.RemoveAsync(pending.ApplicationId, cancellationToken);
                continue;
            }
            pending.DatabaseDeleted = true;
            await DeletePendingFilesAsync(pending, utcNow, cancellationToken);
        }
    }

    private async Task DeletePendingFilesAsync(
        PendingVerificationDraftCleanup pending,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var relativePath in pending.RelativePaths)
            {
                await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
                var referenced = await db.DocumentLibraryFiles.AsNoTracking().AnyAsync(
                        x => x.StorageArea == DocumentStorageAreas.Staging && x.RelativePath == relativePath,
                        cancellationToken) ||
                    await db.VerificationApplicationFiles.AsNoTracking().AnyAsync(
                        x => x.FilePath == relativePath, cancellationToken);
                if (referenced)
                {
                    logger.LogWarning(
                        "Verification draft cleanup preserved referenced file Path={RelativePath} ApplicationId={ApplicationId}.",
                        relativePath, pending.ApplicationId);
                    continue;
                }
                fileSystem.DeleteStagingFile(_stagingRoot, relativePath);
            }
            await journal.RemoveAsync(pending.ApplicationId, cancellationToken);
            logger.LogInformation(
                "Verification draft cleanup file retry completed ApplicationId={ApplicationId} Attempt={Attempt}.",
                pending.ApplicationId, pending.AttemptCount + 1);
        }
        catch (Exception ex)
        {
            pending.AttemptCount++;
            pending.LastAttemptAtUtc = utcNow;
            pending.LastError = ex.GetType().Name + ": " + ex.Message[..Math.Min(ex.Message.Length, 500)];
            await journal.UpsertAsync(pending, CancellationToken.None);
            logger.LogError(ex,
                "Verification draft cleanup file deletion failed; retry retained ApplicationId={ApplicationId} Attempt={Attempt}.",
                pending.ApplicationId, pending.AttemptCount);
        }
    }

    private void LogDryRun(VerificationDraftCleanupReport report) => logger.LogInformation(
        "VERIFICATION_DRAFT_CLEANUP_DRY_RUN EligibleDrafts={EligibleDrafts} Attachments={Attachments} DeletableFiles={DeletableFiles} ProtectedFiles={ProtectedFiles} ExcludedRecent={ExcludedRecent} ExcludedLegacy={ExcludedLegacy} ExcludedNonDraft={ExcludedNonDraft} PendingRetries={PendingRetries} CutoffUtc={CutoffUtc}",
        report.EligibleDrafts, report.EligibleAttachments, report.DeletableFiles, report.ProtectedFiles,
        report.ExcludedRecentDrafts, report.ExcludedLegacyDrafts, report.ExcludedNonDraftApplications,
        report.PendingRetries, report.CutoffUtc);

    private void ValidateOptions()
    {
        if (_options.RetentionDays <= 0) throw new InvalidOperationException("RetentionDays 必須大於 0。");
        if (_options.RunIntervalHours <= 0) throw new InvalidOperationException("RunIntervalHours 必須大於 0。");
        if (string.IsNullOrWhiteSpace(_options.StatePath))
            throw new InvalidOperationException("VerificationDraftCleanup:StatePath 尚未設定。");
    }
}

public sealed class VerificationDraftCleanupBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<VerificationDraftCleanupOptions> options,
    ILogger<VerificationDraftCleanupBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("Verification draft cleanup is disabled.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<VerificationDraftCleanupWorker>()
                    .RunAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Verification draft cleanup run failed."); }

            try
            {
                await Task.Delay(TimeSpan.FromHours(Math.Max(1, options.Value.RunIntervalHours)), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
