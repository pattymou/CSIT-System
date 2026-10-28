using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Services.Approval;

public enum ApprovalUploadDecisionKind { DirectCommit, ApprovalBypassed, Pending }

public sealed record ApprovalUploadDecision(
    ApprovalUploadDecisionKind Kind, string MenuCode, string ModuleCode);

public sealed record ApprovalSourceIdentity(
    string ModuleCode, string ContextType, string ContextId,
    string? ContextDisplay, string? Department, Guid? TeamOptionId, string? TeamCode);

public static class ApprovalSourceMenuBinding
{
    public const string ValidationRequestApplications = "validation_request_applications";
    public const string VerificationModule = "verification";

    public static string? EffectiveModuleCode(string menuCode, string? configuredModuleCode) =>
        !string.IsNullOrWhiteSpace(configuredModuleCode) ? configuredModuleCode
        : string.Equals(menuCode, ValidationRequestApplications, StringComparison.OrdinalIgnoreCase)
            ? VerificationModule
            : null;

    public static bool Matches(string menuCode, string? configuredModuleCode, string moduleCode) =>
        string.Equals(EffectiveModuleCode(menuCode, configuredModuleCode), moduleCode,
            StringComparison.OrdinalIgnoreCase);
}

public sealed record ApprovalCommitResult(string ResultKey);

public interface IThreeLevelFormalUploadService
{
    Task ExecuteFormalAsync(ThreeLevelUploadTarget target, IReadOnlyList<IFormFile> files,
        string? uploadEmp, bool autoApprove, Guid? approvalFileId = null, string? approvedBy = null);
}

public interface IAssetFormalUploadService
{
    Task UploadFilesAsync(string moduleCode, string apparatusId, IReadOnlyList<IFormFile> files,
        string? uploadEmp, Guid? approvalFileId = null);
}

public interface IVendorFormalUploadService
{
    Task<Guid> ArchiveApprovedFileAsync(Guid vendorId, IFormFile file, string uploadedBy,
        Guid approvalOperationId, string approvedBy, DateTime approvedAt,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// CommitAsync must be idempotent by submission.Id. A retry after a process crash must
/// return the same result without creating a second domain row or formal file.
/// </summary>
public interface IApprovalCommitHandler
{
    string ModuleCode { get; }
    Task ValidateAsync(PendingSubmission submission, CancellationToken cancellationToken);
    Task<ApprovalCommitResult> CommitAsync(PendingSubmission submission, CancellationToken cancellationToken);
}

public interface IApprovalCommitHandlerRegistry
{
    bool HasHandler(string? moduleCode);
    IApprovalCommitHandler Resolve(string? moduleCode);
    bool HasAnyHandler { get; }
}

public interface IApprovalStagingRootResolver
{
    Task<string> GetRootAsync(CancellationToken cancellationToken = default);
}

public interface IApprovalStagingStorage
{
    Task<StagedFileMetadata> StoreAsync(Guid submissionId, Stream content, string originalFileName,
        string contentType, long fileSize, CancellationToken cancellationToken = default);
    Task<Stream> OpenReadAsync(Guid submissionId, string storageKey,
        CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid submissionId, string storageKey,
        CancellationToken cancellationToken = default);
}

public sealed record ApprovalReviewScope(bool IsAdmin, IReadOnlySet<Guid> TeamOptionIds)
{
    public bool Includes(Guid teamOptionId) => IsAdmin || TeamOptionIds.Contains(teamOptionId);
}
