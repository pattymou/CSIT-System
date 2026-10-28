using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Services.Approval;

public sealed class VendorUploadApprovalCommitHandler(
    IApprovalStagingStorage staging,
    IVendorFormalUploadService formal,
    IDbContextFactory<AppDbContext> dbFactory) : IApprovalCommitHandler
{
    public string ModuleCode => VendorApprovalSourceResolver.ModuleCode;

    public async Task ValidateAsync(PendingSubmission submission, CancellationToken cancellationToken)
    {
        if (submission.SourceModuleCode != ModuleCode ||
            submission.ContextType != VendorApprovalSourceResolver.ContextType ||
            !Guid.TryParseExact(submission.ContextId, "N", out var vendorId) ||
            submission.ReviewerAccount is null || submission.ReviewedAt is null ||
            submission.StagingStorageKey is null || submission.FileSize is null ||
            submission.FileHash is null || submission.OriginalFileName is null)
            throw new InvalidOperationException("Approved Vendor submission metadata is incomplete.");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Vendors.AsNoTracking().AnyAsync(x => x.Id == vendorId, cancellationToken))
            throw new InvalidOperationException("Approved Vendor no longer exists.");
        await using var stream = await staging.OpenReadAsync(submission.Id,
            submission.StagingStorageKey, cancellationToken);
        if (stream.Length != submission.FileSize ||
            !string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)),
                submission.FileHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Staged Vendor file checksum or length changed.");
    }

    public async Task<ApprovalCommitResult> CommitAsync(PendingSubmission submission,
        CancellationToken cancellationToken)
    {
        await ValidateAsync(submission, cancellationToken);
        var vendorId = Guid.ParseExact(submission.ContextId, "N");
        await using var stream = await staging.OpenReadAsync(submission.Id,
            submission.StagingStorageKey!, cancellationToken);
        var file = new FormFile(stream, 0, submission.FileSize!.Value, "files",
            submission.OriginalFileName!)
        {
            Headers = new HeaderDictionary(),
            ContentType = submission.ContentType ?? "application/octet-stream"
        };
        var id = await formal.ArchiveApprovedFileAsync(vendorId, file,
            submission.SubmittedByAccount, submission.Id, submission.ReviewerAccount!,
            submission.ReviewedAt!.Value, cancellationToken);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var stored = await db.VendorFiles.AsNoTracking().SingleAsync(x => x.Id == id, cancellationToken);
        if (stored.ArchiveStatus != FileArchiveStatuses.Archived ||
            stored.ApprovalOperationId != submission.Id || stored.Sha256 != submission.FileHash ||
            !await db.KmIntegrationOutbox.AsNoTracking().AnyAsync(x =>
                x.EventType == KmIntegrationEventTypes.VendorFileArchived &&
                x.SourceEntityId == id && x.DocumentId == id, cancellationToken))
            throw new InvalidOperationException("Formal Vendor file or KM outbox is incomplete.");
        return new ApprovalCommitResult(id.ToString("N"));
    }
}
