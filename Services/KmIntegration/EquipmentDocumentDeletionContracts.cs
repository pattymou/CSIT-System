namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

// CSIT-owned draft boundary for Equipment document deletion. This is deliberately
// separate from the approved v1 verification/document-library deletion contract.
public sealed record EquipmentDocumentDeletionResource(
    string SourceEntityType, string EquipmentId, Guid ApparatusFileId,
    Guid DocumentId, int Version)
{
    public bool IsValid() => SourceEntityType == "ApparatusFile" &&
        !string.IsNullOrWhiteSpace(EquipmentId) && EquipmentId.Trim().Length <= 30 &&
        ApparatusFileId != Guid.Empty && DocumentId == ApparatusFileId && Version == 1;
}

public sealed record EquipmentDocumentDeletionRequest(
    string ContractName, string ContractVersion, string SourceSystem,
    Guid DeletionRequestId, DateTimeOffset RequestedAt, string ReasonCode,
    EquipmentDocumentDeletionResource Resource)
{
    public bool IsValid() => ContractName == "CSIT.EquipmentDocumentDeletion" &&
        ContractVersion == "draft-1" && SourceSystem == "CSIT" &&
        DeletionRequestId != Guid.Empty && RequestedAt != default &&
        RequestedAt.Offset == TimeSpan.Zero &&
        ReasonCode == "EquipmentAttachmentDeleted" && Resource is not null && Resource.IsValid();
}

public sealed record EquipmentDocumentDeletionCallback(
    string ContractName, string ContractVersion, Guid CallbackId, string SourceSystem,
    Guid DeletionRequestId, string KmDeletionJobId,
    EquipmentDocumentDeletionResource Resource, string Status,
    KmDeletionTimestamps Timestamps, KmDeletionResult? Result, KmDeletionError? Error)
{
    public bool IsValid() => ContractName == "CSIT.EquipmentDocumentDeletion" &&
        ContractVersion == "draft-1" && CallbackId != Guid.Empty &&
        SourceSystem == "CSIT" && DeletionRequestId != Guid.Empty &&
        KmDeletionJobId is { Length: > 0 and <= 150 } &&
        Resource is not null && Resource.IsValid() && Timestamps is not null &&
        (Status switch
        {
            "Processing" => ValidUtc(Timestamps.StartedAt) && Result is null && Error is null,
            "Completed" => ValidUtc(Timestamps.CompletedAt) && Result is
                { CleanupVerified: true, Cleanup: not null } result &&
                ValidUtc(result.VerifiedAt) && result.VerifiedAt <= Timestamps.CompletedAt &&
                result.Cleanup.IsVerified() && Error is null,
            "Failed" => ValidUtc(Timestamps.FailedAt) && Result is null && Error is
                { Code: { Length: > 0 and <= 100 }, Message: { Length: > 0 and <= 500 } },
            _ => false
        });

    private static bool ValidUtc(DateTimeOffset? value) =>
        value is { } timestamp && timestamp != default && timestamp.Offset == TimeSpan.Zero;
}

public interface IEquipmentDocumentDeletionAdapter
{
    Task<KmDeletionDeliveryResult> SubmitAsync(EquipmentDocumentDeletionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class UnavailableEquipmentDocumentDeletionAdapter : IEquipmentDocumentDeletionAdapter
{
    public Task<KmDeletionDeliveryResult> SubmitAsync(EquipmentDocumentDeletionRequest request,
        CancellationToken cancellationToken = default) => Task.FromResult(
            KmDeletionDeliveryResult.Failure(false, "EQUIPMENT_DELETION_CONTRACT_NOT_APPROVED"));
}
