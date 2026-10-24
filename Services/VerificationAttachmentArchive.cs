using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Config;

namespace SIT.DepartmentSystem.Web.Services;

public static class VerificationAttachmentArchiveStatuses
{
    public const string NotApplicable = "NotApplicable";
    public const string Processing = "Processing";
    public const string Failed = "Failed";
    public const string Completed = "Completed";
}

public sealed class PendingVerificationAttachmentArchive
{
    public Guid ApplicationId { get; set; }
    public Guid AttachmentId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public DateTime? LastAttemptAtUtc { get; set; }
    public string? LastError { get; set; }
}

public sealed class VerificationAttachmentArchiveJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;

    public VerificationAttachmentArchiveJournal(IOptions<DocumentStagingOptions> stagingOptions)
    {
        var root = stagingOptions.Value.RootPath?.Trim();
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("DocumentStaging:RootPath 尚未設定。");
        _path = Path.Combine(Path.GetFullPath(root), ".csit-state", "verification-attachment-archive.json");
    }

    public async Task<IReadOnlyList<PendingVerificationAttachmentArchive>> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await ReadUnsafeAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    public async Task EnqueueAsync(
        Guid applicationId,
        IEnumerable<Guid> attachmentIds,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var entries = await ReadUnsafeAsync(cancellationToken);
            var changed = false;
            foreach (var attachmentId in attachmentIds.Distinct())
            {
                if (entries.Any(x => x.AttachmentId == attachmentId)) continue;
                entries.Add(new PendingVerificationAttachmentArchive
                {
                    ApplicationId = applicationId,
                    AttachmentId = attachmentId,
                    CreatedAtUtc = utcNow
                });
                changed = true;
            }
            if (changed) await WriteUnsafeAsync(entries, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task MarkFailureAsync(Guid attachmentId, Exception exception, DateTime utcNow)
    {
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            var entries = await ReadUnsafeAsync(CancellationToken.None);
            var entry = entries.SingleOrDefault(x => x.AttachmentId == attachmentId);
            if (entry is null) return;
            entry.AttemptCount++;
            entry.LastAttemptAtUtc = utcNow;
            var message = exception.GetType().Name + ": " + exception.Message;
            entry.LastError = message[..Math.Min(message.Length, 1000)];
            await WriteUnsafeAsync(entries, CancellationToken.None);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(Guid attachmentId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var entries = await ReadUnsafeAsync(cancellationToken);
            if (entries.RemoveAll(x => x.AttachmentId == attachmentId) > 0)
                await WriteUnsafeAsync(entries, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveApplicationAsync(Guid applicationId)
    {
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            var entries = await ReadUnsafeAsync(CancellationToken.None);
            if (entries.RemoveAll(x => x.ApplicationId == applicationId) > 0)
                await WriteUnsafeAsync(entries, CancellationToken.None);
        }
        finally { _gate.Release(); }
    }

    private async Task<List<PendingVerificationAttachmentArchive>> ReadUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return [];
        await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<List<PendingVerificationAttachmentArchive>>(
            stream, JsonOptions, cancellationToken) ?? [];
    }

    private async Task WriteUnsafeAsync(
        List<PendingVerificationAttachmentArchive> entries,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporaryPath = _path + ".tmp";
        await using (var stream = new FileStream(
                         temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, entries, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporaryPath, _path, overwrite: true);
    }
}

public sealed record VerificationAttachmentArchiveResult(int Completed, int Failed);

public sealed record VerificationAttachmentHistoryItem(
    Guid ApplicationId,
    string ApplicationNo,
    VerificationApplicationStatus Status,
    Guid AttachmentId,
    Guid? DocumentLibraryFileId,
    string? StorageArea);

public sealed record VerificationAttachmentHistoryReport(
    IReadOnlyList<VerificationAttachmentHistoryItem> NonAcceptedWithDocumentLink,
    IReadOnlyList<VerificationAttachmentHistoryItem> AcceptedNotInLibrary);

public sealed class VerificationAttachmentArchiveProcessor(
    IDbContextFactory<AppDbContext> dbFactory,
    IOptions<DocumentStorageOptions> storageOptions,
    IOptions<DocumentStagingOptions> stagingOptions,
    VerificationAttachmentArchiveJournal journal,
    ILogger<VerificationAttachmentArchiveProcessor> logger)
{
    private readonly SemaphoreSlim _processGate = new(1, 1);
    private readonly string _libraryRoot = RequireRoot(storageOptions.Value.RootPath, "DocumentStorage:RootPath");
    private readonly string _stagingRoot = RequireRoot(stagingOptions.Value.RootPath, "DocumentStaging:RootPath");

    public Task EnqueueApprovedApplicationAsync(
        VerificationApplication application,
        DateTime utcNow,
        CancellationToken cancellationToken) =>
        journal.EnqueueAsync(application.Id, application.Files.Select(x => x.Id), utcNow, cancellationToken);

    public Task RemoveApplicationAsync(Guid applicationId) =>
        journal.RemoveApplicationAsync(applicationId);

    public Task<IReadOnlyList<PendingVerificationAttachmentArchive>> ReadPendingAsync(
        CancellationToken cancellationToken = default) => journal.ReadAsync(cancellationToken);

    public async Task<VerificationAttachmentArchiveResult> ProcessApplicationAsync(
        Guid applicationId,
        CancellationToken cancellationToken = default)
    {
        var entries = (await journal.ReadAsync(cancellationToken))
            .Where(x => x.ApplicationId == applicationId)
            .ToList();
        return await ProcessEntriesAsync(entries, cancellationToken);
    }

    public async Task<VerificationAttachmentArchiveResult> ProcessPendingAsync(
        CancellationToken cancellationToken = default) =>
        await ProcessEntriesAsync(await journal.ReadAsync(cancellationToken), cancellationToken);

    public async Task EnqueueHistoricalAcceptedAsync(
        IReadOnlyCollection<Guid> applicationIds,
        bool repairConfirmed,
        CancellationToken cancellationToken = default)
    {
        if (!repairConfirmed)
            throw new InvalidOperationException("Historical attachment repair requires explicit confirmation.");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var applications = await db.VerificationApplications.AsNoTracking()
            .Include(x => x.Files).ThenInclude(x => x.DocumentLibraryFile)
            .Where(x => applicationIds.Contains(x.Id) && x.Status == VerificationApplicationStatus.Accepted)
            .ToListAsync(cancellationToken);
        foreach (var application in applications)
        {
            var pending = application.Files
                .Where(x => x.DocumentLibraryFile?.StorageArea != DocumentStorageAreas.Library)
                .Select(x => x.Id);
            await journal.EnqueueAsync(application.Id, pending, DateTime.UtcNow, cancellationToken);
        }
    }

    public async Task<VerificationAttachmentHistoryReport> AuditHistoryAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var items = await db.VerificationApplicationFiles.AsNoTracking()
            .Where(x => x.VerificationApplication.Status != VerificationApplicationStatus.Draft)
            .Select(x => new VerificationAttachmentHistoryItem(
                x.VerificationApplicationId,
                x.VerificationApplication.ApplicationNo,
                x.VerificationApplication.Status,
                x.Id,
                x.DocumentLibraryFileId,
                x.DocumentLibraryFile == null ? null : x.DocumentLibraryFile.StorageArea))
            .ToListAsync(cancellationToken);
        return new VerificationAttachmentHistoryReport(
            items.Where(x => x.Status != VerificationApplicationStatus.Accepted &&
                             x.DocumentLibraryFileId.HasValue).ToList(),
            items.Where(x => x.Status == VerificationApplicationStatus.Accepted &&
                             x.StorageArea != DocumentStorageAreas.Library).ToList());
    }

    public async Task<int> RemoveHistoricalPrematureLinksAsync(
        IReadOnlyCollection<Guid> applicationIds,
        bool repairConfirmed,
        CancellationToken cancellationToken = default)
    {
        if (!repairConfirmed)
            throw new InvalidOperationException("Historical attachment repair requires explicit confirmation.");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        IDbContextTransaction? transaction = null;
        try
        {
            if (db.Database.IsRelational())
                transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            var files = await db.VerificationApplicationFiles
                .Include(x => x.VerificationApplication)
                .Include(x => x.DocumentLibraryFile)
                .Where(x => applicationIds.Contains(x.VerificationApplicationId) &&
                            x.VerificationApplication.Status != VerificationApplicationStatus.Accepted &&
                            x.DocumentLibraryFileId.HasValue)
                .ToListAsync(cancellationToken);
            var folderIds = files.Where(x => x.DocumentLibraryFile is not null)
                .Select(x => x.DocumentLibraryFile!.FolderId).Distinct().ToList();
            foreach (var file in files)
            {
                var document = file.DocumentLibraryFile;
                file.DocumentLibraryFileId = null;
                file.DocumentLibraryFile = null;
                if (document is not null) db.DocumentLibraryFiles.Remove(document);
            }
            await db.SaveChangesAsync(cancellationToken);
            foreach (var folderId in folderIds)
                await RemoveEmptyFolderChainAsync(db, folderId, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return files.Count;
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
    }

    private static async Task RemoveEmptyFolderChainAsync(
        AppDbContext db,
        Guid folderId,
        CancellationToken cancellationToken)
    {
        Guid? currentId = folderId;
        while (currentId.HasValue)
        {
            var folder = await db.DocumentFolders.SingleOrDefaultAsync(
                x => x.Id == currentId.Value, cancellationToken);
            if (folder is null) return;
            var hasFiles = await db.DocumentLibraryFiles.AnyAsync(
                x => x.FolderId == folder.Id, cancellationToken);
            var hasChildren = await db.DocumentFolders.AnyAsync(
                x => x.ParentId == folder.Id, cancellationToken);
            if (hasFiles || hasChildren) return;
            currentId = folder.ParentId;
            db.DocumentFolders.Remove(folder);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<VerificationAttachmentArchiveResult> ProcessEntriesAsync(
        IReadOnlyCollection<PendingVerificationAttachmentArchive> entries,
        CancellationToken cancellationToken)
    {
        await _processGate.WaitAsync(cancellationToken);
        try
        {
            var completed = 0;
            var failed = 0;
            foreach (var entry in entries)
            {
                try
                {
                    await ArchiveAsync(entry, cancellationToken);
                    await journal.RemoveAsync(entry.AttachmentId, cancellationToken);
                    completed++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    failed++;
                    await journal.MarkFailureAsync(entry.AttachmentId, ex, DateTime.UtcNow);
                    logger.LogError(ex,
                        "Verification attachment archive failed ApplicationId={ApplicationId} AttachmentId={AttachmentId}.",
                        entry.ApplicationId, entry.AttachmentId);
                }
            }
            return new VerificationAttachmentArchiveResult(completed, failed);
        }
        finally { _processGate.Release(); }
    }

    private async Task ArchiveAsync(
        PendingVerificationAttachmentArchive entry,
        CancellationToken cancellationToken)
    {
        await using var readDb = await dbFactory.CreateDbContextAsync(cancellationToken);
        var application = await readDb.VerificationApplications.AsNoTracking()
            .Include(x => x.Files).ThenInclude(x => x.DocumentLibraryFile)
            .SingleOrDefaultAsync(x => x.Id == entry.ApplicationId, cancellationToken)
            ?? throw new InvalidOperationException("The approved verification application no longer exists.");
        if (application.Status != VerificationApplicationStatus.Accepted)
            throw new InvalidOperationException("The verification application approval has not committed.");
        var attachment = application.Files.SingleOrDefault(x => x.Id == entry.AttachmentId)
            ?? throw new InvalidOperationException("The approved verification attachment no longer exists.");

        var modelName = ValidateModelName(application.ProductModel);
        var expectedSha = ValidateSha256(attachment);
        var standardFileName = DocumentLibraryRules.CreateStandardFileName(
            attachment.FileName, attachment.CreatedAt, attachment.Id);
        var relativePath = Path.Combine(
            VerificationApplicationAttachmentFolders.Root,
            modelName,
            VerificationApplicationAttachmentFolders.ApplicationDocuments,
            standardFileName);
        var finalPath = ResolveWithinRoot(_libraryRoot, relativePath);
        var sourcePath = ResolveWithinRoot(_stagingRoot, attachment.FilePath);

        if (File.Exists(finalPath))
        {
            await VerifyFileAsync(finalPath, attachment.FileSize, expectedSha, cancellationToken);
        }
        else
        {
            await VerifyFileAsync(sourcePath, attachment.FileSize, expectedSha, cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            var temporaryPath = finalPath + $".archive-{attachment.Id:N}.tmp";
            try
            {
                await CopyAndVerifyAsync(
                    sourcePath, temporaryPath, attachment.FileSize, expectedSha, cancellationToken);
                try { File.Move(temporaryPath, finalPath, overwrite: false); }
                catch (IOException) when (File.Exists(finalPath))
                {
                    await VerifyFileAsync(finalPath, attachment.FileSize, expectedSha, cancellationToken);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        IDbContextTransaction? transaction = null;
        try
        {
            if (db.Database.IsRelational())
                transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            var lockedApplication = await db.VerificationApplications
                .Include(x => x.Files).ThenInclude(x => x.DocumentLibraryFile)
                .SingleAsync(x => x.Id == entry.ApplicationId, cancellationToken);
            if (lockedApplication.Status != VerificationApplicationStatus.Accepted)
                throw new InvalidOperationException("The verification application is no longer accepted.");
            var lockedAttachment = lockedApplication.Files.Single(x => x.Id == entry.AttachmentId);
            var archivedAt = DateTime.UtcNow;
            var document = await VerificationApplicationAttachmentLinker.LinkFormalAsync(
                db, lockedApplication, lockedAttachment, standardFileName, relativePath, DateTime.UtcNow,
                cancellationToken);
            var hasKmEvent = await db.KmIntegrationOutbox.AnyAsync(x =>
                x.SourceSystem == "CSIT" &&
                x.EventType == KmIntegrationEventTypes.VerificationApplicationAttachmentArchived &&
                x.SourceEntityType == KmIntegrationSourceEntityTypes.VerificationApplicationFile &&
                x.SourceEntityId == lockedAttachment.Id && x.DocumentId == document.Id && x.Version == 1,
                cancellationToken);
            if (!hasKmEvent)
                db.KmIntegrationOutbox.Add(KmIntegrationOutbox.ForArchivedVerificationAttachment(
                    lockedApplication, lockedAttachment, archivedAt));
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

        await VerifyFileAsync(finalPath, attachment.FileSize, expectedSha, cancellationToken);
        if (!string.Equals(sourcePath, finalPath, StringComparison.OrdinalIgnoreCase) && File.Exists(sourcePath))
            File.Delete(sourcePath);
        logger.LogInformation(
            "VERIFICATION_ATTACHMENT_ARCHIVE_COMPLETE ApplicationId={ApplicationId} AttachmentId={AttachmentId} RelativePath={RelativePath}",
            entry.ApplicationId, entry.AttachmentId, relativePath);
    }

    internal static string ValidateModelName(string? value)
    {
        var name = DocumentLibraryRules.NormalizeName(value);
        if (name is "." or ".." || Path.IsPathRooted(name) ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar) ||
            name.EndsWith(' ') || name.EndsWith('.'))
            throw new InvalidOperationException("Model Name contains characters that are unsafe for a Windows folder.");
        var stem = name.Split('.')[0];
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }
            .Contains(stem, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Model Name is a reserved Windows folder name.");
        return name;
    }

    private static string ValidateSha256(VerificationApplicationFile attachment)
    {
        var sha = attachment.Sha256?.Trim().ToLowerInvariant();
        if (sha is null || sha.Length != 64 || sha.Any(x => !Uri.IsHexDigit(x)))
            throw new InvalidOperationException($"Attachment {attachment.Id} has an invalid SHA256 value.");
        return sha;
    }

    private static string RequireRoot(string? configured, string name)
    {
        if (string.IsNullOrWhiteSpace(configured)) throw new InvalidOperationException($"{name} 尚未設定。");
        var root = Path.GetFullPath(configured.Trim());
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"{name} does not exist: {root}");
        return root;
    }

    private static string ResolveWithinRoot(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidOperationException("Attachment storage path is invalid.");
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Attachment storage path escapes its configured root.");
        return fullPath;
    }

    private static async Task CopyAndVerifyAsync(
        string sourcePath,
        string destinationPath,
        long expectedLength,
        string expectedSha,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        await using var destination = new FileStream(
            destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            hash.AppendData(buffer, 0, read);
        }
        await destination.FlushAsync(cancellationToken);
        destination.Flush(flushToDisk: true);
        var actualSha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (total != expectedLength || !string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The copied verification attachment failed size or SHA256 validation.");
    }

    private static async Task VerifyFileAsync(
        string path,
        long expectedLength,
        string expectedSha,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Verification attachment file does not exist.", path);
        if (info.Length != expectedLength)
            throw new InvalidDataException("Verification attachment size does not match its database record.");
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var actualSha = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        if (!string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Verification attachment SHA256 does not match its database record.");
    }
}

public sealed class VerificationAttachmentArchiveBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<VerificationAttachmentArchiveBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<VerificationAttachmentArchiveProcessor>()
                    .ProcessPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Verification attachment archive retry cycle failed."); }

            try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
