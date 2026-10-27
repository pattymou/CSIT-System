using System.Security.Claims;

namespace SIT.DepartmentSystem.Web.Services.Approval;

public sealed record ThreeLevelUploadResult(ApprovalUploadDecisionKind Decision, int FileCount)
{
    public IReadOnlyList<Guid> SubmissionIds { get; init; } = [];
}

/// <summary>Routes trusted ThreeLevel resources through the menu policy before formal storage is touched.</summary>
public sealed class ThreeLevelApprovalUploadService(
    ApprovalSourceResolver sources,
    ApprovalPolicyResolver policy,
    ApprovalSubmissionService submissions,
    IApprovalStagingStorage staging,
    IThreeLevelFormalUploadService formal)
{
    public Task<ThreeLevelUploadResult> UploadAsync(ThreeLevelUploadTarget target,
        IReadOnlyList<IFormFile> files, ClaimsPrincipal uploader, string? uploadEmp, bool legacyAutoApprove,
        CancellationToken cancellationToken = default) => UploadCoreAsync(target, files, uploader,
            uploadEmp, legacyAutoApprove, null, cancellationToken);

    public async Task<Guid> ResubmitAsync(ThreeLevelUploadTarget target, IFormFile file,
        ClaimsPrincipal uploader, Guid rejectedSubmissionId, CancellationToken cancellationToken = default)
    {
        var result = await UploadCoreAsync(target, [file], uploader, null, false,
            rejectedSubmissionId, cancellationToken);
        return result.SubmissionIds.Single();
    }

    private async Task<ThreeLevelUploadResult> UploadCoreAsync(ThreeLevelUploadTarget target,
        IReadOnlyList<IFormFile> files, ClaimsPrincipal uploader, string? uploadEmp, bool legacyAutoApprove,
        Guid? resubmissionOfId, CancellationToken cancellationToken)
    {
        if (files.Count == 0) throw new InvalidOperationException("沒有收到任何檔案。");
        if (target.Kind == ThreeLevelUploadKind.TestReportVersion && files.Count != 1)
            throw new InvalidOperationException("上傳新版時一次只能選擇一個檔案。");
        if ((target.Kind is ThreeLevelUploadKind.TestReport or ThreeLevelUploadKind.TestReportByTaskNo)
            && files.Count != 1)
            throw new InvalidOperationException("第一次上傳測試報告時一次只能選擇一個檔案。");
        foreach (var file in files)
        {
            if (file.Length is <= 0 or > 200_000_000)
                throw new InvalidOperationException("上傳檔案必須有內容，且不得超過 200 MB。");
            if (string.IsNullOrWhiteSpace(Path.GetFileName(file.FileName)) ||
                Path.GetFileName(file.FileName).Length > 255)
                throw new InvalidOperationException("檔名無效或過長。");
        }

        var actor = string.IsNullOrWhiteSpace(uploadEmp) ? ApprovalIdentity.Account(uploader) : uploadEmp;
        var moduleCode = await sources.GetModuleCodeAsync(target, cancellationToken);

        // These menu/module identities have a registered ThreeLevel approval handler.
        // Other ThreeLevel modules retain their existing formal upload path.
        if (!string.Equals(moduleCode, "certification", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(moduleCode, "verification", StringComparison.OrdinalIgnoreCase))
        {
            if (resubmissionOfId.HasValue)
                throw new InvalidOperationException("此來源目前無法重新送審，請從原項目重新上傳。");
            await formal.ExecuteFormalAsync(target, files, actor, legacyAutoApprove);
            return new ThreeLevelUploadResult(ApprovalUploadDecisionKind.DirectCommit, files.Count);
        }

        var sourceMenuCode = string.Equals(moduleCode, ApprovalSourceMenuBinding.VerificationModule,
                StringComparison.OrdinalIgnoreCase) &&
            target.Kind is (ThreeLevelUploadKind.TestReport or ThreeLevelUploadKind.TestReportByTaskNo
                or ThreeLevelUploadKind.TestReportVersion)
            ? ApprovalSourceMenuBinding.ValidationRequestApplications
            : null;
        var decision = await policy.ResolveAsync(new ApprovalSourceIdentity(moduleCode,
            target.ContextType, target.ContextId, null, null, null, null), uploader, cancellationToken,
            sourceMenuCode);
        var expectedMenuCode = sourceMenuCode ?? moduleCode;
        if (!string.Equals(decision.MenuCode, expectedMenuCode, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("ThreeLevel Menu Code mapping changed; upload was stopped.");
        if (resubmissionOfId.HasValue && decision.Kind != ApprovalUploadDecisionKind.Pending)
            throw new InvalidOperationException("此來源目前不需主管審核，請從原項目重新上傳。");
        if (decision.Kind != ApprovalUploadDecisionKind.Pending)
        {
            await formal.ExecuteFormalAsync(target, files, actor,
                decision.Kind == ApprovalUploadDecisionKind.ApprovalBypassed || legacyAutoApprove);
            if (decision.Kind == ApprovalUploadDecisionKind.ApprovalBypassed)
                foreach (var _ in files)
                    await submissions.RecordBypassAsync(Guid.NewGuid(), decision, uploader, cancellationToken);
            return new ThreeLevelUploadResult(decision.Kind, files.Count);
        }

        var source = await ResolveSourceAsync(target, cancellationToken);
        if (source.TeamOptionId is null || source.TeamOptionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(source.TeamCode) || string.IsNullOrWhiteSpace(source.Department))
            throw new InvalidOperationException("此專案尚未設定 Team，無法送主管審核，請先完成 Team 設定。");

        source = source with { ContextType = target.ContextType, ContextId = target.ContextId };
        var submissionIds = new List<Guid>(files.Count);
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
                submissionIds.Add(id);
            }
            catch
            {
                try { await staging.DeleteAsync(id, staged.StorageKey, cancellationToken); }
                catch { /* Preserve the submission error; orphan cleanup can run separately. */ }
                throw;
            }
        }
        return new ThreeLevelUploadResult(ApprovalUploadDecisionKind.Pending, files.Count)
            { SubmissionIds = submissionIds };
    }

    private async Task<ApprovalSourceIdentity> ResolveSourceAsync(ThreeLevelUploadTarget target,
        CancellationToken cancellationToken) => target.Kind switch
    {
        ThreeLevelUploadKind.CaseAttachment => await sources.ForCaseAsync(target.Id, cancellationToken),
        ThreeLevelUploadKind.CaseAttachmentByNo => await sources.ForRecordAsync(target.Id, target.Number!, cancellationToken),
        ThreeLevelUploadKind.TaskAttachment or ThreeLevelUploadKind.TestReport =>
            await sources.ForTaskAsync(target.Id, cancellationToken),
        ThreeLevelUploadKind.TaskAttachmentByNo or ThreeLevelUploadKind.TestReportByTaskNo =>
            await sources.ForCaseTaskNoAsync(target.Id, target.Number!, cancellationToken),
        ThreeLevelUploadKind.TestReportVersion => await sources.ForFileAsync(target.Id, cancellationToken),
        _ => throw new InvalidOperationException("Unsupported ThreeLevel upload kind.")
    };
}
