using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Services.Approval;

/// <summary>Reusable ThreeLevel handler registered for each enabled source module.</summary>
public sealed class ThreeLevelUploadApprovalCommitHandler(
    string moduleCode,
    ApprovalSourceResolver sources,
    IApprovalStagingStorage staging,
    IThreeLevelFormalUploadService formal,
    IDbContextFactory<AppDbContext> dbFactory,
    AppDbContext? activeDb = null) : IApprovalCommitHandler
{
    public string ModuleCode { get; } = moduleCode;

    public async Task ValidateAsync(PendingSubmission submission, CancellationToken cancellationToken)
    {
        if (!string.Equals(submission.SourceModuleCode, ModuleCode, StringComparison.OrdinalIgnoreCase)
            || submission.ReviewerAccount is null || submission.ReviewedAt is null
            || submission.StagingStorageKey is null || submission.FileSize is null
            || submission.FileHash is null || submission.OriginalFileName is null)
            throw new InvalidOperationException("Approved ThreeLevel submission metadata is incomplete.");
        var target = ThreeLevelUploadTarget.FromContext(submission.ContextType, submission.ContextId);
        var actualModule = await sources.GetModuleCodeAsync(target, cancellationToken);
        if (!string.Equals(actualModule, ModuleCode, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Approved resource no longer belongs to the source module.");
        await using var stream = await staging.OpenReadAsync(submission.Id,
            submission.StagingStorageKey, cancellationToken);
        if (stream.Length != submission.FileSize ||
            !string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)),
                submission.FileHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Staged file checksum or length changed.");
    }

    public async Task<ApprovalCommitResult> CommitAsync(PendingSubmission submission,
        CancellationToken cancellationToken)
    {
        await ValidateAsync(submission, cancellationToken);
        var target = ThreeLevelUploadTarget.FromContext(submission.ContextType, submission.ContextId);
        await using var stream = await staging.OpenReadAsync(submission.Id,
            submission.StagingStorageKey!, cancellationToken);
        var file = new FormFile(stream, 0, submission.FileSize!.Value, "files", submission.OriginalFileName!)
        {
            Headers = new HeaderDictionary(),
            ContentType = submission.ContentType ?? "application/octet-stream"
        };
        await formal.ExecuteFormalAsync(target, [file], submission.SubmittedByAccount,
            autoApprove: true, approvalFileId: submission.Id, approvedBy: submission.ReviewerAccount);

        await using AppDbContext? ownedDb = activeDb is null
            ? await dbFactory.CreateDbContextAsync(cancellationToken) : null;
        var db = activeDb ?? ownedDb!;
        var result = await db.ModuleCaseFiles
            .SingleAsync(x => x.Id == submission.Id, cancellationToken);
        if (!string.Equals(result.Sha256, submission.FileHash, StringComparison.OrdinalIgnoreCase)
            || !result.IsRawDataExported || !File.Exists(Path.Combine(result.FilePath, result.FileName)))
            throw new InvalidOperationException("Formal file or RAW export is incomplete.");
        await using var committed = File.OpenRead(Path.Combine(result.FilePath, result.FileName));
        if (!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(committed, cancellationToken)),
            submission.FileHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Formal file checksum does not match staging.");
        if (target.Kind is ThreeLevelUploadKind.TestReport or ThreeLevelUploadKind.TestReportByTaskNo
            or ThreeLevelUploadKind.TestReportVersion)
        {
            if (result.FileKind != ModuleCaseFileKinds.TestReport ||
                result.ReviewStatus != TestReportReviewStatuses.Approved)
                throw new InvalidOperationException("Approved test report entered legacy pending review.");
        }
        return new ApprovalCommitResult(result.Id.ToString("N"));
    }
}
