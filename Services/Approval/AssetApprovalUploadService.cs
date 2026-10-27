using System.Security.Claims;

namespace SIT.DepartmentSystem.Web.Services.Approval;

/// <summary>Routes a server resolved Asset through its Menu policy before formal storage is touched.</summary>
public sealed class AssetApprovalUploadService(
    ApprovalSourceResolver sources,
    ApprovalPolicyResolver policy,
    ApprovalSubmissionService submissions,
    IApprovalStagingStorage staging,
    IAssetFormalUploadService formal)
{
    public async Task<ApprovalUploadDecisionKind> UploadAsync(string routeModuleCode, string assetId,
        IReadOnlyList<IFormFile> files, ClaimsPrincipal uploader,
        CancellationToken cancellationToken = default)
    {
        var result = await UploadCoreAsync(routeModuleCode, assetId, files, uploader, null, cancellationToken);
        return result.Decision;
    }

    public async Task<Guid> ResubmitAsync(string routeModuleCode, string assetId, IFormFile file,
        ClaimsPrincipal uploader, Guid rejectedSubmissionId, CancellationToken cancellationToken = default)
    {
        var result = await UploadCoreAsync(routeModuleCode, assetId, [file], uploader,
            rejectedSubmissionId, cancellationToken);
        return result.SubmissionId ?? throw new InvalidOperationException("重新送審未建立新提交。");
    }

    private async Task<(ApprovalUploadDecisionKind Decision, Guid? SubmissionId)> UploadCoreAsync(
        string routeModuleCode, string assetId, IReadOnlyList<IFormFile> files, ClaimsPrincipal uploader,
        Guid? resubmissionOfId, CancellationToken cancellationToken)
    {
        if (files.Count == 0) throw new InvalidOperationException("沒有收到檔案。");
        var moduleCode = await sources.GetAssetModuleCodeAsync(assetId, cancellationToken);
        if (!string.Equals(moduleCode, routeModuleCode, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("上傳路由與實際資產 Module 不一致。");

        // Asset modules without this integration retain their existing direct upload path.
        if (!string.Equals(moduleCode, "equipment", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(moduleCode, "goods", StringComparison.OrdinalIgnoreCase))
        {
            if (resubmissionOfId.HasValue)
                throw new InvalidOperationException("此來源目前無法重新送審，請從原項目重新上傳。");
            await formal.UploadFilesAsync(moduleCode, assetId, files, Actor(uploader));
            return (ApprovalUploadDecisionKind.DirectCommit, null);
        }

        var decision = await policy.ResolveAsync(new ApprovalSourceIdentity(moduleCode,
            "Asset", assetId, null, null, null, null), uploader, cancellationToken);
        if (!string.Equals(decision.MenuCode, moduleCode, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Asset Menu Code mapping changed; upload was stopped.");
        if (resubmissionOfId.HasValue && decision.Kind != ApprovalUploadDecisionKind.Pending)
            throw new InvalidOperationException("此來源目前不需主管審核，請從原項目重新上傳。");
        if (decision.Kind != ApprovalUploadDecisionKind.Pending)
        {
            // Keep the existing formal upload behavior when the menu flag is off.
            await formal.UploadFilesAsync(moduleCode, assetId, files, Actor(uploader));
            if (decision.Kind == ApprovalUploadDecisionKind.ApprovalBypassed)
                foreach (var _ in files)
                    await submissions.RecordBypassAsync(Guid.NewGuid(), decision, uploader, cancellationToken);
            return (decision.Kind, null);
        }

        var source = await sources.ForAssetAsync(moduleCode, assetId, cancellationToken);
        if (source.TeamOptionId is null || source.TeamOptionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(source.TeamCode))
            throw new InvalidOperationException("此項目尚未設定 Team，無法送主管審核，請先完成 Team 設定。");
        if (string.IsNullOrWhiteSpace(source.Department))
            throw new InvalidOperationException("此項目尚未設定 Department，無法送主管審核，請先完成 Department 設定。");

        Guid? newId = null;
        foreach (var file in files)
        {
            if (file.Length is <= 0 or > 200_000_000 ||
                string.IsNullOrWhiteSpace(Path.GetFileName(file.FileName)) ||
                Path.GetFileName(file.FileName).Length > 255)
                throw new InvalidOperationException("上傳檔案必須有內容、檔名有效，且不得超過 200 MB。");
        }
        foreach (var file in files)
        {
            var id = Guid.NewGuid();
            await using var stream = file.OpenReadStream();
            var staged = await staging.StoreAsync(id, stream, file.FileName,
                file.ContentType, file.Length, cancellationToken);
            try
            {
                await submissions.CreatePendingAsync(id, decision, source, id.ToString("N"),
                    uploader, staged, cancellationToken, resubmissionOfId);
                newId = id;
            }
            catch
            {
                try { await staging.DeleteAsync(id, staged.StorageKey, cancellationToken); }
                catch { /* Preserve the submission error; orphan cleanup can run separately. */ }
                throw;
            }
        }
        return (ApprovalUploadDecisionKind.Pending, newId);
    }

    private static string Actor(ClaimsPrincipal user) =>
        user.FindFirstValue("account") ?? user.Identity?.Name ?? "SYSTEM";
}
