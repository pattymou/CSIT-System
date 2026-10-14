using System.Security.Claims;

namespace SIT.DepartmentSystem.Web.Services.Approval;

/// <summary>Reuses the trusted upload entry points for a rejected submission's original context.</summary>
public sealed class MySubmissionWorkspaceService(
    ApprovalSubmissionService submissions,
    ThreeLevelApprovalUploadService threeLevel,
    AssetApprovalUploadService assets)
{
    public async Task<Guid> ResubmitAsync(Guid rejectedId, IFormFile file, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var old = await submissions.GetActiveRejectedMineAsync(rejectedId, user, cancellationToken);
        if (old.ContextType == "Asset")
            return await assets.ResubmitAsync(old.SourceModuleCode, old.ContextId, file,
                user, rejectedId, cancellationToken);

        var target = ThreeLevelUploadTarget.FromContext(old.ContextType, old.ContextId);
        return await threeLevel.ResubmitAsync(target, file, user, rejectedId, cancellationToken);
    }
}
