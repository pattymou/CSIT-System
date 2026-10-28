using System.Security.Claims;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Services.Approval;

public sealed record VendorUploadOutcome(
    ApprovalUploadDecisionKind Decision,
    IReadOnlyList<Guid> OperationIds);

public sealed class VendorApprovalUploadService(
    ApprovalPolicyResolver policy,
    VendorApprovalSourceResolver sources,
    ApprovalSubmissionService submissions,
    IApprovalStagingStorage staging,
    IVendorFormalUploadService formal)
{
    public async Task<VendorUploadOutcome> UploadAsync(Guid vendorId,
        IReadOnlyList<IFormFile> files, ClaimsPrincipal uploader,
        CancellationToken cancellationToken = default)
    {
        if (files.Count == 0) throw new InvalidOperationException("沒有收到檔案。");
        Validate(files);
        var decision = await policy.ResolveAsync(new ApprovalSourceIdentity(
            VendorApprovalSourceResolver.ModuleCode, VendorApprovalSourceResolver.ContextType,
            vendorId.ToString("N"), null, null, null, null), uploader, cancellationToken,
            VendorApprovalSourceResolver.MenuCode);
        var ids = new List<Guid>(files.Count);
        if (decision.Kind != ApprovalUploadDecisionKind.Pending)
        {
            foreach (var file in files)
            {
                var operationId = Guid.NewGuid();
                if (decision.Kind == ApprovalUploadDecisionKind.ApprovalBypassed)
                    await submissions.RecordBypassAsync(operationId, decision, uploader, cancellationToken);
                await formal.ArchiveApprovedFileAsync(vendorId, file, ApprovalIdentity.Account(uploader),
                    operationId, ApprovalIdentity.Account(uploader), DateTime.UtcNow, cancellationToken);
                ids.Add(operationId);
            }
            return new VendorUploadOutcome(decision.Kind, ids);
        }

        var source = await sources.ResolveAsync(vendorId, uploader, cancellationToken);
        foreach (var file in files)
        {
            var id = Guid.NewGuid();
            await using var stream = file.OpenReadStream();
            var staged = await staging.StoreAsync(id, stream, file.FileName,
                file.ContentType, file.Length, cancellationToken);
            try
            {
                await submissions.CreatePendingAsync(id, decision, source, id.ToString("N"),
                    uploader, staged, cancellationToken);
                ids.Add(id);
            }
            catch
            {
                try { await staging.DeleteAsync(id, staged.StorageKey, cancellationToken); }
                catch { }
                throw;
            }
        }
        return new VendorUploadOutcome(ApprovalUploadDecisionKind.Pending, ids);
    }

    public async Task<Guid> ResubmitAsync(Guid vendorId, IFormFile file, ClaimsPrincipal uploader,
        Guid rejectedSubmissionId, CancellationToken cancellationToken = default)
    {
        Validate([file]);
        var decision = await policy.ResolveAsync(new ApprovalSourceIdentity(
            VendorApprovalSourceResolver.ModuleCode, VendorApprovalSourceResolver.ContextType,
            vendorId.ToString("N"), null, null, null, null), uploader, cancellationToken,
            VendorApprovalSourceResolver.MenuCode);
        if (decision.Kind != ApprovalUploadDecisionKind.Pending)
            throw new InvalidOperationException("此來源目前不需主管審核，無法從退回項目重新送審。");
        var source = await sources.ResolveAsync(vendorId, uploader, cancellationToken);
        var id = Guid.NewGuid();
        await using var stream = file.OpenReadStream();
        var staged = await staging.StoreAsync(id, stream, file.FileName,
            file.ContentType, file.Length, cancellationToken);
        try
        {
            await submissions.CreatePendingAsync(id, decision, source, id.ToString("N"),
                uploader, staged, cancellationToken, rejectedSubmissionId);
            return id;
        }
        catch
        {
            try { await staging.DeleteAsync(id, staged.StorageKey, cancellationToken); }
            catch { }
            throw;
        }
    }

    private static void Validate(IEnumerable<IFormFile> files)
    {
        foreach (var file in files)
        {
            var name = ManagedAssetFileStorage.ValidateOriginalFileName(file.FileName, file.Length);
            if (name.Length > 255)
                throw new InvalidOperationException("送審附件檔名不可超過 255 個字元。");
        }
    }
}
