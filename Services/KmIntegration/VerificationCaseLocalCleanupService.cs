using System.Data;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Config;
using SIT.DepartmentSystem.Web.Services.Approval;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public sealed class VerificationCaseLocalCleanupService(
    AppDbContext db,
    IBusinessWriteAuthorizationGuard businessWrite,
    IAuthorizationService authorization,
    IOptions<KmDeletionOptions> deletionOptions,
    IOptions<DocumentStorageOptions> libraryOptions,
    IOptions<DocumentStagingOptions> stagingOptions,
    IOptions<RawDataSettings> rawDataOptions,
    VerificationAttachmentArchiveProcessor archive)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<VerificationCaseDeletionJob> BeginAsync(Guid recordId, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        if (!deletionOptions.Value.Enabled)
            throw new InvalidOperationException("Case deletion is not enabled.");
        await businessWrite.DemandAsync(cancellationToken);
        if (!(await authorization.AuthorizeAsync(user, SystemAuthorization.Policies.Administration)).Succeeded)
            throw new UnauthorizedAccessException("Local deletion cleanup requires Administration approval.");
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken) : null;
        await VerificationCaseDeletionWriteGuard.LockRecordAsync(db, recordId, cancellationToken);
        var job = await db.VerificationCaseDeletionJobs.Include(x => x.Targets)
            .SingleAsync(x => x.RecordId == recordId, cancellationToken);
        if (job.Status is "Completed" or "LocalCleanup") return job;
        if (job.Status is not ("LocalReady" or "KmVerified") ||
            job.Targets.Any(x => x.Status is not ("NotDispatched" or "Completed")) ||
            job.Targets.Any(x => x.Status == "Completed" && x.KmVerifiedAt is null))
            throw new InvalidOperationException("KM deletion is not fully verified for every delivered document.");
        var application = await db.VerificationApplications.AsNoTracking()
            .SingleAsync(x => x.Id == job.ApplicationId && x.ModuleRecordId == recordId &&
                x.Status == VerificationApplicationStatus.Accepted, cancellationToken);
        VerificationApplicationSecurity.EnsureAcceptedManagerAccess(application, user);
        var reports = await db.ModuleCaseFiles.AsNoTracking().Where(x => x.RecordId == recordId)
            .ToListAsync(cancellationToken);
        if ((await archive.ReadPendingAsync(cancellationToken)).Any(x => x.ApplicationId == job.ApplicationId))
            throw new InvalidOperationException("Attachment archive work is still pending.");

        var manifest = await BuildManifestAsync(job, application, reports, cancellationToken);
        var now = DateTime.UtcNow;
        job.MarkLocalCleanup(JsonSerializer.Serialize(manifest, JsonOptions), now);
        db.ApprovalAuditEvents.Add(ApprovalAuditEvent.Create(job.Id, "verification", "verification",
            ApprovalAuditActions.CaseDeletionCleanupStarted,
            VerificationApplicationSecurity.GetAccount(user), now, null, null,
            details: $"ApplicationId={job.ApplicationId};RecordId={job.RecordId};Files={manifest.Count}"));
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return job;
    }

    public async Task<VerificationCaseDeletionJob> ProcessAsync(Guid recordId, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        if (!deletionOptions.Value.Enabled)
            throw new InvalidOperationException("Case deletion is not enabled.");
        await businessWrite.DemandAsync(cancellationToken);
        if (!(await authorization.AuthorizeAsync(user, SystemAuthorization.Policies.Administration)).Succeeded)
            throw new UnauthorizedAccessException("Local deletion cleanup requires Administration approval.");
        // A session advisory lock spans the DB commit and physical file cleanup. A crashed
        // process releases it automatically, and the durable manifest permits a retry.
        var postgres = db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL";
        var lockKey = BitConverter.ToInt64(recordId.ToByteArray(), 0) ^ 0x4353495444454C45L;
        if (postgres)
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT pg_try_advisory_lock(@key)";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "key"; parameter.Value = lockKey;
            command.Parameters.Add(parameter);
            if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            {
                await db.Database.CloseConnectionAsync();
                throw new InvalidOperationException("Local cleanup is already running for this case.");
            }
        }
        try { return await ProcessLockedAsync(recordId, user, cancellationToken); }
        finally
        {
            if (postgres)
            {
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = "SELECT pg_advisory_unlock(@key)";
                var parameter = command.CreateParameter();
                parameter.ParameterName = "key"; parameter.Value = lockKey;
                command.Parameters.Add(parameter);
                await command.ExecuteScalarAsync(CancellationToken.None);
                await db.Database.CloseConnectionAsync();
            }
        }
    }

    private async Task<VerificationCaseDeletionJob> ProcessLockedAsync(Guid recordId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var job = await db.VerificationCaseDeletionJobs.Include(x => x.Targets)
            .SingleAsync(x => x.RecordId == recordId, cancellationToken);
        if (job.Status == "Completed") return job;
        if (job.Status != "LocalCleanup" || job.CleanupManifestJson is null)
            throw new InvalidOperationException("Local cleanup has not been prepared.");
        var actor = VerificationApplicationSecurity.GetAccount(user);
        var manifest = JsonSerializer.Deserialize<List<VerificationCaseDeletionPhysicalFile>>(
            job.CleanupManifestJson, JsonOptions)
            ?? throw new InvalidOperationException("Local cleanup manifest is invalid.");

        // Detect changed files and new cross-case references before removing the DB inventory.
        // On a retry, already removed files are intentionally absent from the manifest check.
        try
        {
            await RejectSharedPathsAsync(job, manifest, cancellationToken);
            foreach (var file in manifest)
                await VerificationCaseDeletionPhysicalFiles.VerifyAsync(file,
                    allowMissing: job.DbCleanedAt is not null, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            job.MarkCleanupFailed("FILE_PREFLIGHT_FAILED", DateTime.UtcNow);
            AddCleanupAudit(job, actor, ApprovalAuditActions.CaseDeletionCleanupFailed,
                "FILE_PREFLIGHT_FAILED");
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        if (job.DbCleanedAt is null)
        {
            try { await CleanDatabaseAsync(job, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
                job = await db.VerificationCaseDeletionJobs.SingleAsync(x => x.RecordId == recordId,
                    CancellationToken.None);
                job.MarkCleanupFailed("DB_CLEANUP_FAILED", DateTime.UtcNow);
                AddCleanupAudit(job, actor, ApprovalAuditActions.CaseDeletionCleanupFailed,
                    "DB_CLEANUP_FAILED");
                await db.SaveChangesAsync(CancellationToken.None);
                throw;
            }
        }
        try
        {
            await RejectSharedPathsAsync(job, manifest, cancellationToken);
            foreach (var file in manifest)
                await VerificationCaseDeletionPhysicalFiles.DeleteAsync(file, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            job.MarkCleanupFailed(ex is UnauthorizedAccessException ? "FILE_ACCESS_DENIED" :
                ex is IOException ? "FILE_IO_FAILED" : "FILE_CHANGED_OR_UNSAFE", DateTime.UtcNow);
            AddCleanupAudit(job, actor, ApprovalAuditActions.CaseDeletionCleanupFailed,
                job.LastCleanupErrorCode!);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        job.MarkCompleted(DateTime.UtcNow);
        AddCleanupAudit(job, actor, ApprovalAuditActions.CaseDeletionCompleted,
            $"Files={manifest.Count};DbCleanedAt={job.DbCleanedAt:O}");
        await db.SaveChangesAsync(cancellationToken);
        return job;
    }

    private void AddCleanupAudit(VerificationCaseDeletionJob job, string actor,
        string action, string details) =>
        db.ApprovalAuditEvents.Add(ApprovalAuditEvent.Create(job.Id, "verification", "verification",
            action, actor, DateTime.UtcNow, null, null, details: details));

    private async Task CleanDatabaseAsync(VerificationCaseDeletionJob job,
        CancellationToken cancellationToken)
    {
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken) : null;
        await VerificationCaseDeletionWriteGuard.LockRecordAsync(db, job.RecordId, cancellationToken);
        db.CaseDeletionCleanupJobId = job.Id;
        try
        {
            var application = await db.VerificationApplications.SingleOrDefaultAsync(x =>
                x.Id == job.ApplicationId && x.ModuleRecordId == job.RecordId,
                cancellationToken) ?? throw new InvalidOperationException("Deletion application identity changed.");
            var record = await db.ModuleRecords.SingleAsync(x => x.Id == job.RecordId,
                cancellationToken);
            var cases = await db.ModuleRecordCases.Where(x => x.RecordId == job.RecordId)
                .ToListAsync(cancellationToken);
            var caseIds = cases.Select(x => x.Id).ToList();
            var tasks = await db.ModuleRecordTasks.Where(x => caseIds.Contains(x.CaseId))
                .ToListAsync(cancellationToken);
            var files = await db.ModuleCaseFiles.Where(x => x.RecordId == job.RecordId)
                .ToListAsync(cancellationToken);
            var attachments = await db.VerificationApplicationFiles.Where(x =>
                x.VerificationApplicationId == job.ApplicationId).ToListAsync(cancellationToken);
            var documentIds = attachments.Where(x => x.DocumentLibraryFileId.HasValue)
                .Select(x => x.DocumentLibraryFileId!.Value).ToList();
            var documents = await db.DocumentLibraryFiles.Where(x => documentIds.Contains(x.Id))
                .ToListAsync(cancellationToken);
            if (documents.Count != documentIds.Count ||
                await db.VerificationApplicationFiles.AsNoTracking().AnyAsync(x =>
                    x.VerificationApplicationId != job.ApplicationId &&
                    x.DocumentLibraryFileId.HasValue &&
                    documentIds.Contains(x.DocumentLibraryFileId.Value), cancellationToken))
                throw new InvalidOperationException("An archived document has a shared or missing link.");
            var processing = await db.DocumentProcessingJobs.Where(x =>
                documentIds.Contains(x.DocumentFileId)).ToListAsync(cancellationToken);
            if (processing.Any(x => x.Status is DocumentProcessingStatuses.Pending or
                DocumentProcessingStatuses.Processing))
                throw new InvalidOperationException("Document processing is still active.");
            var sourceIds = files.Select(x => x.Id).Concat(attachments.Select(x => x.Id)).ToList();
            var outbox = await db.KmIntegrationOutbox.Where(x =>
                sourceIds.Contains(x.SourceEntityId)).ToListAsync(cancellationToken);
            if (outbox.Any(x => x.Status == KmIntegrationStatus.Dispatching) ||
                outbox.Any(x => x.KmJobId is not null &&
                    job.Targets.All(t => t.SourceEntityId != x.SourceEntityId || t.Status != "Completed")))
                throw new InvalidOperationException("KM outbox delivery remains unverified.");
            var keys = VerificationCaseDeletionPreviewService.BuildApprovalContextKeys(
                job.RecordId, cases, tasks, files);
            var contextIds = keys.Select(x => x.ContextId).Distinct().ToList();
            var candidates = await db.PendingSubmissions.Where(x =>
                x.SourceModuleCode == "verification" && contextIds.Contains(x.ContextId))
                .ToListAsync(cancellationToken);
            var submissions = candidates.Where(x => keys.Contains((x.ContextType, x.ContextId))).ToList();
            if (submissions.Any(x => x.Status is ApprovalStatus.Pending or
                ApprovalStatus.ApprovedPendingCommit or ApprovalStatus.CommitFailed))
                throw new InvalidOperationException("An approval submission remains active.");

            db.PendingSubmissions.RemoveRange(submissions);
            db.KmIntegrationOutbox.RemoveRange(outbox);
            db.DocumentProcessingJobs.RemoveRange(processing);
            db.VerificationApplicationFiles.RemoveRange(attachments);
            db.DocumentLibraryFiles.RemoveRange(documents);
            db.ModuleCaseFiles.RemoveRange(files);
            db.ModuleRecordTasks.RemoveRange(tasks);
            db.ModuleRecordCases.RemoveRange(cases);
            db.VerificationApplications.Remove(application);
            db.ModuleRecords.Remove(record);
            await db.SaveChangesAsync(cancellationToken);
            job.MarkDbCleaned(DateTime.UtcNow);
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        }
        finally { db.CaseDeletionCleanupJobId = null; }
    }

    private async Task<List<VerificationCaseDeletionPhysicalFile>> BuildManifestAsync(
        VerificationCaseDeletionJob job, VerificationApplication application,
        List<ModuleCaseFile> files, CancellationToken cancellationToken)
    {
        var roots = await db.SystemOptions.AsNoTracking().Where(x => x.IsEnabled &&
            (x.Category == "ThreeLevelRootPath" || x.Category == "TestReportRootPath"))
            .Select(x => x.Value).ToListAsync(cancellationToken);
        roots = roots.Where(Path.IsPathFullyQualified).Select(Path.GetFullPath).ToList();
        var libraryRoot = RequireRoot(libraryOptions.Value.RootPath);
        var stagingRoot = RequireRoot(stagingOptions.Value.RootPath);
        var nasRoot = RequireRoot(rawDataOptions.Value.NasRootPath);
        var result = new List<VerificationCaseDeletionPhysicalFile>();
        async Task AddAsync(string kind, Guid id, string root, string path, bool optional = false)
        {
            var safe = VerificationCaseDeletionPhysicalFiles.Validate(root, path);
            if (optional && !File.Exists(safe)) return;
            if (result.Any(x => string.Equals(x.Path, safe, StringComparison.OrdinalIgnoreCase))) return;
            result.Add(await VerificationCaseDeletionPhysicalFiles.InspectAsync(
                kind, id, root, safe, cancellationToken));
        }
        foreach (var file in files)
        {
            var full = Path.GetFullPath(Path.Combine(file.FilePath, SafeFileName(file.FileName)));
            var root = roots.FirstOrDefault(x => IsWithin(x, full))
                ?? throw new InvalidOperationException("A case file is outside configured storage roots.");
            await AddAsync("ModuleCaseFile", file.Id, root, full);
            var driveRoot = Path.GetPathRoot(full)
                ?? throw new InvalidOperationException("A case file drive root cannot be resolved.");
            await AddAsync("NasMirror", file.Id, nasRoot,
                Path.Combine(nasRoot, full[driveRoot.Length..].TrimStart(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), optional: true);
            if (!string.IsNullOrWhiteSpace(file.NasFilePath))
                await AddAsync("NasMirror", file.Id, nasRoot, file.NasFilePath, optional: true);
            if (!string.IsNullOrWhiteSpace(file.RawJsonPath))
            {
                var rawPath = VerificationCaseDeletionPhysicalFiles.Validate(nasRoot, file.RawJsonPath);
                if (File.Exists(rawPath))
                {
                    using var raw = JsonDocument.Parse(await File.ReadAllTextAsync(rawPath, cancellationToken));
                    if (!raw.RootElement.TryGetProperty("project", out var project) ||
                        !project.TryGetProperty("id", out var id) ||
                        id.GetString() != job.RecordId.ToString())
                        throw new InvalidOperationException("Raw Data index is not exclusively owned by this case.");
                    await AddAsync("RawDataIndex", file.Id, nasRoot, rawPath);
                }
            }
        }
        var record = await db.ModuleRecords.AsNoTracking().Where(x => x.Id == job.RecordId)
            .Select(x => new { x.Name, ModuleCode = x.Module.Code })
            .SingleAsync(cancellationToken);
        var projectRoot = await db.SystemOptions.AsNoTracking().Where(x => x.IsEnabled &&
            x.Category == "ThreeLevelRootPath" &&
            x.Name.ToLower() == record.ModuleCode.ToLower())
            .Select(x => x.Value).SingleOrDefaultAsync(cancellationToken);
        if (rawDataOptions.Value.Enabled)
        {
            var localRoot = RequireRoot(projectRoot);
            var projectFolder = VerificationCaseDeletionPhysicalFiles.Validate(localRoot,
                Path.Combine(localRoot, CleanFolderName(record.Name)));
            var driveRoot = Path.GetPathRoot(projectFolder)
                ?? throw new InvalidOperationException("Project storage root cannot be resolved.");
            var rawFolder = VerificationCaseDeletionPhysicalFiles.Validate(nasRoot,
                Path.Combine(nasRoot, projectFolder[driveRoot.Length..].TrimStart(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            foreach (var name in new[] { "rawdata.json", "km-manifest.json" })
            {
                var path = VerificationCaseDeletionPhysicalFiles.Validate(nasRoot,
                    Path.Combine(rawFolder, name));
                if (!File.Exists(path)) continue;
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));
                if (!json.RootElement.TryGetProperty("source", out var source) ||
                    !source.TryGetProperty("entityId", out var sourceId) ||
                    sourceId.GetString() != job.RecordId.ToString())
                    throw new InvalidOperationException("Project Raw Data index belongs to another case.");
                await AddAsync(name == "rawdata.json" ? "RawDataIndex" : "KmManifestIndex",
                    job.RecordId, nasRoot, path);
            }
            EnsureNoUntrackedFiles(nasRoot, rawFolder, result);
        }
        var attachments = await db.VerificationApplicationFiles.AsNoTracking()
            .Include(x => x.DocumentLibraryFile)
            .Where(x => x.VerificationApplicationId == application.Id)
            .ToListAsync(cancellationToken);
        foreach (var attachment in attachments)
        {
            var staged = Path.Combine(stagingRoot, attachment.FilePath);
            await AddAsync("ApplicationStaging", attachment.Id, stagingRoot, staged,
                optional: attachment.DocumentLibraryFileId.HasValue);
            if (attachment.DocumentLibraryFile is { } document)
                await AddAsync("DocumentLibraryFile", document.Id, libraryRoot,
                    Path.Combine(libraryRoot, document.RelativePath));
        }
        var applicationStaging = VerificationCaseDeletionPhysicalFiles.Validate(stagingRoot,
            Path.Combine(stagingRoot, "verification-applications", application.Id.ToString("N")));
        EnsureNoUntrackedFiles(stagingRoot, applicationStaging, result);
        var cases = await db.ModuleRecordCases.AsNoTracking().Where(x => x.RecordId == job.RecordId)
            .ToListAsync(cancellationToken);
        var caseIds = cases.Select(x => x.Id).ToList();
        var tasks = await db.ModuleRecordTasks.AsNoTracking().Where(x => caseIds.Contains(x.CaseId))
            .ToListAsync(cancellationToken);
        var keys = VerificationCaseDeletionPreviewService.BuildApprovalContextKeys(
            job.RecordId, cases, tasks, files);
        var contextIds = keys.Select(x => x.ContextId).Distinct().ToList();
        var submissions = await db.PendingSubmissions.AsNoTracking().Where(x =>
            x.SourceModuleCode == "verification" && contextIds.Contains(x.ContextId))
            .ToListAsync(cancellationToken);
        var approvalRoot = await db.SystemOptions.AsNoTracking().Where(x =>
            x.IsEnabled && x.Category == "ApprovalStagingRoot" && x.Name == "default")
            .Select(x => x.Value).SingleOrDefaultAsync(cancellationToken);
        foreach (var submission in submissions.Where(x => keys.Contains((x.ContextType, x.ContextId))))
        {
            if (submission.Status is ApprovalStatus.Pending or ApprovalStatus.ApprovedPendingCommit or
                ApprovalStatus.CommitFailed)
                throw new InvalidOperationException("An approval submission remains active.");
            if (submission.StagingStorageKey is { } key)
            {
                var pieces = key.Split('/');
                if (pieces.Length != 2 || pieces[0] != submission.Id.ToString("N") ||
                    !Guid.TryParseExact(pieces[1], "N", out _))
                    throw new InvalidOperationException("Approval staging key is invalid.");
                var root = RequireRoot(approvalRoot);
                await AddAsync("ApprovalStaging", submission.Id, root,
                    Path.Combine(root, pieces[0], pieces[1]), optional: true);
            }
        }
        await RejectSharedPathsAsync(job, result, cancellationToken);
        return result;
    }

    private async Task RejectSharedPathsAsync(VerificationCaseDeletionJob job,
        List<VerificationCaseDeletionPhysicalFile> manifest, CancellationToken cancellationToken)
    {
        var others = await db.ModuleCaseFiles.AsNoTracking().Where(x => x.RecordId != job.RecordId)
            .Select(x => new { x.FilePath, x.FileName, x.NasFilePath, x.RawJsonPath })
            .ToListAsync(cancellationToken);
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var other in others)
        {
            referenced.Add(Path.GetFullPath(Path.Combine(other.FilePath, other.FileName)));
            if (!string.IsNullOrWhiteSpace(other.NasFilePath)) referenced.Add(Path.GetFullPath(other.NasFilePath));
            if (!string.IsNullOrWhiteSpace(other.RawJsonPath)) referenced.Add(Path.GetFullPath(other.RawJsonPath));
        }
        var otherAttachments = await db.VerificationApplicationFiles.AsNoTracking()
            .Where(x => x.VerificationApplicationId != job.ApplicationId)
            .Select(x => new { x.FilePath, x.DocumentLibraryFileId })
            .ToListAsync(cancellationToken);
        var stagingRoot = RequireRoot(stagingOptions.Value.RootPath);
        foreach (var attachment in otherAttachments)
            referenced.Add(Path.GetFullPath(Path.Combine(stagingRoot, attachment.FilePath)));
        var libraryRoot = RequireRoot(libraryOptions.Value.RootPath);
        var ownDocumentIds = manifest.Where(x => x.Kind == "DocumentLibraryFile")
            .Select(x => x.SourceId).ToList();
        var otherDocuments = await db.DocumentLibraryFiles.AsNoTracking()
            .Where(x => !ownDocumentIds.Contains(x.Id)).Select(x => x.RelativePath)
            .ToListAsync(cancellationToken);
        foreach (var path in otherDocuments)
            referenced.Add(Path.GetFullPath(Path.Combine(libraryRoot, path)));
        if (manifest.Any(x => referenced.Contains(x.Path)))
            throw new InvalidOperationException("A physical file or Raw Data index is shared with another case.");
        var rawFolders = manifest.Where(x => x.Kind is "RawDataIndex" or "KmManifestIndex")
            .Select(x => Path.GetFileName(Path.GetDirectoryName(x.Path)))
            .Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (rawFolders.Count > 0)
        {
            var otherNames = await db.ModuleRecords.AsNoTracking().Where(x => x.Id != job.RecordId)
                .Select(x => x.Name).ToListAsync(cancellationToken);
            if (otherNames.Any(x => rawFolders.Contains(CleanFolderName(x))))
                throw new InvalidOperationException("A project Raw Data path may be shared with another record.");
        }
    }

    private static string RequireRoot(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value)
            ? Path.GetFullPath(value) : throw new InvalidOperationException("A storage root is not configured.");

    private static string SafeFileName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name == Path.GetFileName(name)
            ? name : throw new InvalidOperationException("A case file name is unsafe.");

    private static string CleanFolderName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string((value ?? string.Empty)
            .Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "Unknown" : cleaned.Trim();
    }

    private static bool IsWithin(string root, string path) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static void EnsureNoUntrackedFiles(string root, string folder,
        List<VerificationCaseDeletionPhysicalFile> manifest)
    {
        if (!Directory.Exists(folder)) return;
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.TryPop(out var current))
        {
            VerificationCaseDeletionPhysicalFiles.Validate(root, current);
            foreach (var path in Directory.EnumerateFileSystemEntries(current))
            {
                var safe = VerificationCaseDeletionPhysicalFiles.Validate(root, path);
                if (Directory.Exists(safe)) pending.Push(safe);
                else if (!manifest.Any(x => string.Equals(x.Path, safe,
                    StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Case storage contains a file without an exact record.");
            }
        }
    }
}
