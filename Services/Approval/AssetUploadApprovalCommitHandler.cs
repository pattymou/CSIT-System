using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Services.Approval;

/// <summary>Shared Asset commit handler; registered once per stable module code.</summary>
public sealed class AssetUploadApprovalCommitHandler(
    string moduleCode,
    ApprovalSourceResolver sources,
    IApprovalStagingStorage staging,
    IAssetFormalUploadService formal,
    IDbContextFactory<AppDbContext> dbFactory) : IApprovalCommitHandler
{
    public string ModuleCode { get; } = moduleCode;

    public async Task ValidateAsync(PendingSubmission submission, CancellationToken cancellationToken)
    {
        if (!string.Equals(submission.SourceModuleCode, ModuleCode, StringComparison.OrdinalIgnoreCase)
            || submission.ContextType != "Asset" || string.IsNullOrWhiteSpace(submission.ContextId)
            || submission.ReviewerAccount is null || submission.ReviewedAt is null
            || submission.StagingStorageKey is null || submission.FileSize is null
            || submission.FileHash is null || submission.OriginalFileName is null)
            throw new InvalidOperationException("Approved Asset submission metadata is incomplete.");
        var actualModule = await sources.GetAssetModuleCodeAsync(submission.ContextId, cancellationToken);
        if (!string.Equals(actualModule, ModuleCode, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Approved Asset no longer belongs to the source module.");
        await using var stream = await staging.OpenReadAsync(submission.Id,
            submission.StagingStorageKey, cancellationToken);
        if (stream.Length != submission.FileSize ||
            !string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)),
                submission.FileHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Staged Asset file checksum or length changed.");
    }

    public async Task<ApprovalCommitResult> CommitAsync(PendingSubmission submission,
        CancellationToken cancellationToken)
    {
        await ValidateAsync(submission, cancellationToken);
        await using var stream = await staging.OpenReadAsync(submission.Id,
            submission.StagingStorageKey!, cancellationToken);
        var file = new FormFile(stream, 0, submission.FileSize!.Value, "files", submission.OriginalFileName!)
        {
            Headers = new HeaderDictionary(),
            ContentType = submission.ContentType ?? "application/octet-stream"
        };
        await formal.UploadFilesAsync(ModuleCode, submission.ContextId, [file],
            submission.SubmittedByAccount, submission.Id);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var result = await db.ApparatusFiles.AsNoTracking()
            .SingleAsync(x => x.Id == submission.Id, cancellationToken);
        if (result.ApparatusId != submission.ContextId || result.FileSize != submission.FileSize ||
            !File.Exists(Path.Combine(result.FilePath, result.FileName)) ||
            !result.IsRawDataExported || string.IsNullOrWhiteSpace(result.NasFolderPath) ||
            string.IsNullOrWhiteSpace(result.RawJsonPath) || !File.Exists(result.RawJsonPath))
            throw new InvalidOperationException("Formal Asset file or RAW export is incomplete.");
        var formalPath = Path.Combine(result.FilePath, result.FileName);
        var mirrorPath = Path.Combine(result.NasFolderPath, result.Id.ToString("N"), result.FileName);
        if (!File.Exists(mirrorPath))
            throw new InvalidOperationException("Formal Asset RAW mirror is missing.");
        await using var formalFile = File.OpenRead(formalPath);
        await using var mirrorFile = File.OpenRead(mirrorPath);
        var formalHash = Convert.ToHexString(await SHA256.HashDataAsync(formalFile, cancellationToken));
        var mirrorHash = Convert.ToHexString(await SHA256.HashDataAsync(mirrorFile, cancellationToken));
        if (!string.Equals(formalHash, submission.FileHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(mirrorHash, submission.FileHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Formal Asset file or RAW mirror checksum changed.");
        return new ApprovalCommitResult(result.Id.ToString("N"));
    }
}
