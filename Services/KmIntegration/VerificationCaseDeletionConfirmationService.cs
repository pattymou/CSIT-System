using System.Data;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Services.Approval;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

// A POST confirmation is the only transition from a review draft to a durable case hold.
public sealed class VerificationCaseDeletionConfirmationService(
    AppDbContext db,
    VerificationCaseDeletionPreparationService preparation,
    VerificationAttachmentArchiveProcessor archive,
    IOptions<KmDeletionOptions> options)
{
    public async Task<VerificationCaseDeletionJob> ConfirmAsync(Guid recordId,
        string applicationNo, string reason, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled)
            throw new InvalidOperationException("Case deletion is not enabled.");
        var prepared = await preparation.PrepareReviewAsync(recordId, user, reason, cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken) : null;
        await VerificationCaseDeletionWriteGuard.LockRecordAsync(db, recordId, cancellationToken);
        var job = await db.VerificationCaseDeletionJobs.Include(x => x.Targets)
            .SingleAsync(x => x.Id == prepared.Id, cancellationToken);
        var application = await db.VerificationApplications.AsNoTracking().SingleAsync(x =>
            x.Id == job.ApplicationId && x.ModuleRecordId == recordId &&
            x.Status == VerificationApplicationStatus.Accepted, cancellationToken);
        VerificationApplicationSecurity.EnsureAcceptedManagerAccess(application, user);
        if (!string.Equals(application.ApplicationNo, applicationNo?.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException("Application number confirmation does not match.");
        if (job.Status != "Prepared") return job;

        var cases = await db.ModuleRecordCases.AsNoTracking().Where(x => x.RecordId == recordId)
            .ToListAsync(cancellationToken);
        var caseIds = cases.Select(x => x.Id).ToList();
        var tasks = await db.ModuleRecordTasks.AsNoTracking().Where(x => caseIds.Contains(x.CaseId))
            .ToListAsync(cancellationToken);
        var files = await db.ModuleCaseFiles.AsNoTracking().Where(x => x.RecordId == recordId)
            .ToListAsync(cancellationToken);
        var attachments = await db.VerificationApplicationFiles.AsNoTracking()
            .Include(x => x.DocumentLibraryFile)
            .Where(x => x.VerificationApplicationId == application.Id)
            .ToListAsync(cancellationToken);
        if ((await archive.ReadPendingAsync(cancellationToken)).Any(x => x.ApplicationId == application.Id))
            throw new InvalidOperationException("An attachment archive job is still pending.");
        if (attachments.Any(x => x.DocumentLibraryFileId.HasValue &&
            x.DocumentLibraryFile?.StorageArea != DocumentStorageAreas.Library))
            throw new InvalidOperationException("An archived attachment link is incomplete.");

        var contextKeys = VerificationCaseDeletionPreviewService.BuildApprovalContextKeys(
            recordId, cases, tasks, files);
        var contextIds = contextKeys.Select(x => x.ContextId).Distinct().ToList();
        var submissions = await db.PendingSubmissions.AsNoTracking().Where(x =>
            x.SourceModuleCode == "verification" && contextIds.Contains(x.ContextId))
            .ToListAsync(cancellationToken);
        if (submissions.Any(x => contextKeys.Contains((x.ContextType, x.ContextId)) &&
            x.Status is ApprovalStatus.Pending or ApprovalStatus.ApprovedPendingCommit or
                ApprovalStatus.CommitFailed))
            throw new InvalidOperationException("An approval submission is pending or incomplete.");

        var archivedIds = attachments.Where(x => x.DocumentLibraryFileId.HasValue)
            .Select(x => x.DocumentLibraryFileId!.Value).ToList();
        if (await db.DocumentProcessingJobs.AsNoTracking().AnyAsync(x =>
            archivedIds.Contains(x.DocumentFileId) &&
            (x.Status == DocumentProcessingStatuses.Pending ||
             x.Status == DocumentProcessingStatuses.Processing), cancellationToken))
            throw new InvalidOperationException("A document processing job is still active.");

        var expected = files.Where(x => x.FileKind == ModuleCaseFileKinds.TestReport &&
                x.ReviewStatus == TestReportReviewStatuses.Approved && x.IsRawDataExported)
            .Select(x => KmDeletionResourceFactory.ForTestReport(application, x))
            .Concat(attachments.Where(x => x.DocumentLibraryFileId.HasValue)
                .Select(x => KmDeletionResourceFactory.ForArchivedAttachment(application, x)))
            .ToHashSet();
        if (expected.Count != job.Targets.Count ||
            job.Targets.Any(x => !expected.Contains(x.Resource)))
            throw new InvalidOperationException("The deletion inventory changed; prepare a fresh review.");

        var sourceIds = files.Select(x => x.Id).Concat(attachments.Select(x => x.Id)).ToList();
        var outboxIds = await db.KmIntegrationOutbox.AsNoTracking()
            .Where(x => sourceIds.Contains(x.SourceEntityId))
            .OrderBy(x => x.Id).Select(x => x.Id).ToListAsync(cancellationToken);
        var outbox = new List<KmIntegrationOutbox>();
        foreach (var id in outboxIds)
        {
            var row = db.Database.IsRelational()
                ? await db.KmIntegrationOutbox.FromSqlInterpolated($"SELECT * FROM km_integration_outbox WHERE id = {id} FOR UPDATE SKIP LOCKED")
                    .SingleOrDefaultAsync(cancellationToken)
                : await db.KmIntegrationOutbox.SingleAsync(x => x.Id == id, cancellationToken);
            if (row is null)
                throw new InvalidOperationException("KM outbox is being dispatched or retried.");
            outbox.Add(row);
        }
        if (outbox.Any(x => x.Status == KmIntegrationStatus.Dispatching))
            throw new InvalidOperationException("KM dispatch is in progress or its delivery outcome is unknown.");
        if (outbox.Any(x => job.Targets.All(t => t.SourceEntityId != x.SourceEntityId)))
            throw new InvalidOperationException("KM outbox contains an unverified case document.");

        var now = DateTime.UtcNow;
        foreach (var target in job.Targets)
        {
            var rows = outbox.Where(x => x.SourceEntityId == target.SourceEntityId).ToList();
            if (rows.Count > 1) throw new InvalidOperationException("Duplicate KM outbox source.");
            var row = rows.SingleOrDefault();
            if (row is null)
                throw new InvalidOperationException("KM delivery history is missing for a formal document.");
            if (row.Status == KmIntegrationStatus.Pending &&
                row.LastAttemptAt is null && row.KmJobId is null && row.RetryCount == 0)
                target.MarkNotDispatched(now);
        }
        if (job.Targets.All(x => x.Status == "NotDispatched"))
            job.MarkLocalReady(now);
        else
            job.MarkWaitingForKm(now);
        db.ApprovalAuditEvents.Add(ApprovalAuditEvent.Create(job.Id, "verification", "verification",
            ApprovalAuditActions.CaseDeletionFrozen,
            VerificationApplicationSecurity.GetAccount(user), now, null, null,
            reason: job.Reason,
            details: $"ApplicationId={job.ApplicationId};RecordId={job.RecordId};Targets={job.Targets.Count};Status={job.Status}"));
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return job;
    }
}
