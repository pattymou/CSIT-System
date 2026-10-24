using System.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Config;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Authorize(Policy = KmIntegrationAuthorization.Policy)]
[Route("api/integrations/km/v1")]
public sealed class KmIntegrationController(
    IDbContextFactory<AppDbContext> dbFactory,
    ILogger<KmIntegrationController> logger,
    IOptions<DocumentStorageOptions>? storageOptions = null) : ControllerBase
{
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
