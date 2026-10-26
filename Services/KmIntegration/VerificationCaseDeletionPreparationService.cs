using System.Data;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

// Persists a review draft only. There is deliberately no controller or activation path.
public sealed class VerificationCaseDeletionPreparationService(
    AppDbContext db, IBusinessWriteAuthorizationGuard businessWrite)
{
    public async Task<VerificationCaseDeletionJob> PrepareReviewAsync(Guid recordId,
        ClaimsPrincipal user, string reason, CancellationToken cancellationToken = default)
    {
        if (!user.HasClaim(SystemAuthorization.AccessScopeClaim,
            SystemAuthorization.AccessScopes.CsitStaff))
            throw new UnauthorizedAccessException("CSIT staff access is required.");
        await businessWrite.DemandAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken) : null;
        var application = await db.VerificationApplications.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Status == VerificationApplicationStatus.Accepted && x.ModuleRecordId == recordId,
            cancellationToken) ?? throw new KeyNotFoundException("Accepted case was not found.");
        VerificationApplicationSecurity.EnsureAcceptedManagerAccess(application, user);
        if (!await db.ModuleRecords.AsNoTracking().AnyAsync(x => x.Id == recordId, cancellationToken))
            throw new InvalidOperationException("Approved ModuleRecord is missing.");
        var existing = await db.VerificationCaseDeletionJobs.Include(x => x.Targets)
            .SingleOrDefaultAsync(x => x.RecordId == recordId, cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.Reason, reason?.Trim(), StringComparison.Ordinal))
                throw new InvalidOperationException("A deletion review already exists with a different reason.");
            return existing;
        }

        var files = await db.ModuleCaseFiles.AsNoTracking().Where(x => x.RecordId == recordId)
            .ToListAsync(cancellationToken);
        var documentIds = files.Where(x => x.DocumentId != Guid.Empty).Select(x => x.DocumentId)
            .Distinct().ToList();
        if (await db.ModuleCaseFiles.AsNoTracking().AnyAsync(x =>
            x.RecordId != recordId && documentIds.Contains(x.DocumentId), cancellationToken))
            throw new InvalidOperationException("A report document ID is shared with another case.");
        var attachments = await db.VerificationApplicationFiles.AsNoTracking()
            .Include(x => x.DocumentLibraryFile)
            .Where(x => x.VerificationApplicationId == application.Id)
            .ToListAsync(cancellationToken);
        if (attachments.Any(x => x.DocumentLibraryFileId.HasValue &&
            x.DocumentLibraryFile?.StorageArea != DocumentStorageAreas.Library))
            throw new InvalidOperationException("An archived attachment link is inconsistent.");

        var now = DateTime.UtcNow;
        var job = VerificationCaseDeletionJob.Prepare(application.Id, recordId,
            VerificationApplicationSecurity.GetAccount(user), reason, now);
        foreach (var file in files.Where(x => x.FileKind == ModuleCaseFileKinds.TestReport &&
            x.ReviewStatus == TestReportReviewStatuses.Approved && x.IsRawDataExported))
            job.AddTarget(KmDeletionResourceFactory.ForTestReport(application, file), now);
        foreach (var attachment in attachments.Where(x => x.DocumentLibraryFileId.HasValue))
            job.AddTarget(KmDeletionResourceFactory.ForArchivedAttachment(application, attachment), now);
        var sourceIds = files.Select(x => x.Id).Concat(attachments.Select(x => x.Id)).ToList();
        var kmSourceIds = await db.KmIntegrationOutbox.AsNoTracking()
            .Where(x => sourceIds.Contains(x.SourceEntityId))
            .Select(x => x.SourceEntityId).Distinct().ToListAsync(cancellationToken);
        if (kmSourceIds.Any(id => job.Targets.All(x => x.SourceEntityId != id)))
            throw new InvalidOperationException("KM outbox references an unverified case file.");
        db.VerificationCaseDeletionJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return job;
    }
}
