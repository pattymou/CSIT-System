namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public static class KmDocumentDeletionV2SourceTypes
{
    public const string VendorFile = "VendorFile";
    public const string EquipmentAttachment = "EquipmentAttachment";
}

public static class KmDocumentDeletionReasonCodes
{
    public const string SourceDeleted = "SourceDeleted";
    public const string VersionSuperseded = "VersionSuperseded";
    public const string AdministrativeCleanup = "AdministrativeCleanup";

    public static bool IsValid(string value) => value is SourceDeleted or
        VersionSuperseded or AdministrativeCleanup;
}

// Every property is intentionally present in JSON. Source-specific identifiers that do not
// apply remain null; this keeps the v1.4.1 deletion resource shape exact and unambiguous.
public sealed record KmDocumentDeletionV2Resource(
    string SourceType, Guid SourceEntityId, Guid DocumentId, int Version, string Sha256,
    Guid? ApplicationId, Guid? RecordId, Guid? VendorId, string? EquipmentId,
    Guid CanonicalIngestionEventId, string Scope)
{
    public bool IsValid()
    {
        if (SourceEntityId == Guid.Empty || DocumentId != SourceEntityId || Version < 1 ||
            Sha256 is not { Length: 64 } || !Sha256.All(Uri.IsHexDigit) ||
            CanonicalIngestionEventId == Guid.Empty || Scope != "DocumentVersion") return false;
        return SourceType switch
        {
            KmDocumentDeletionV2SourceTypes.VendorFile => Version == 1 &&
                VendorId is { } vendorId && vendorId != Guid.Empty &&
                ApplicationId is null && RecordId is null && EquipmentId is null,
            KmDocumentDeletionV2SourceTypes.EquipmentAttachment => Version == 1 &&
                !string.IsNullOrWhiteSpace(EquipmentId) && EquipmentId.Trim().Length <= 30 &&
                ApplicationId is null && RecordId is null && VendorId is null,
            _ => false
        };
    }
}

public sealed record KmDocumentDeletionV2Request(
    string SchemaVersion, string SourceSystem, string EventType, Guid DeletionRequestId,
    DateTimeOffset RequestedAt, string ReasonCode, KmDocumentDeletionV2Resource Resource)
{
    public bool IsValid() => SchemaVersion == "2.0" && SourceSystem == "CSIT" &&
        EventType == "DocumentDeletionRequested" && DeletionRequestId != Guid.Empty &&
        RequestedAt != default && RequestedAt.Offset == TimeSpan.Zero &&
        KmDocumentDeletionReasonCodes.IsValid(ReasonCode) && Resource is not null &&
        Resource.IsValid();
}

public sealed record KmDocumentDeletionV2Callback(
    string SchemaVersion, Guid CallbackId, string SourceSystem, Guid DeletionRequestId,
    string KmDeletionJobId, KmDocumentDeletionV2Resource Resource, string Status,
    KmDeletionTimestamps Timestamps, KmDeletionResult? Result, KmDeletionError? Error)
{
    public bool IsValid() => SchemaVersion == "2.0" && CallbackId != Guid.Empty &&
        SourceSystem == "CSIT" && DeletionRequestId != Guid.Empty &&
        KmDeletionJobId is { Length: > 0 and <= 150 } && Resource is not null &&
        Resource.IsValid() && Timestamps is not null && Status switch
        {
            "Processing" => ValidUtc(Timestamps.StartedAt) && Timestamps.CompletedAt is null &&
                Timestamps.FailedAt is null && Result is null && Error is null,
            "Completed" => OptionalUtcBefore(Timestamps.StartedAt, Timestamps.CompletedAt) &&
                ValidUtc(Timestamps.CompletedAt) && Timestamps.FailedAt is null && Result is
                { CleanupVerified: true, Cleanup: not null } result &&
                ValidUtc(result.VerifiedAt) && result.VerifiedAt <= Timestamps.CompletedAt &&
                result.Cleanup.IsVerified() && Error is null,
            "Failed" => OptionalUtcBefore(Timestamps.StartedAt, Timestamps.FailedAt) &&
                ValidUtc(Timestamps.FailedAt) && Timestamps.CompletedAt is null &&
                Result is null && Error is
                { Code: { Length: > 0 and <= 100 }, Message: { Length: > 0 and <= 500 } },
            _ => false
        };

    private static bool ValidUtc(DateTimeOffset? value) => value is { } timestamp &&
        timestamp != default && timestamp.Offset == TimeSpan.Zero;
    private static bool OptionalUtcBefore(DateTimeOffset? earlier, DateTimeOffset? later) =>
        earlier is null || ValidUtc(earlier) && later.HasValue && earlier <= later;
}

public interface IKmDocumentDeletionV2Client
{
    Task<KmDeletionDeliveryResult> SubmitAsync(KmDocumentDeletionV2Request request,
        CancellationToken cancellationToken = default);
}

public sealed class UnavailableKmDocumentDeletionV2Client : IKmDocumentDeletionV2Client
{
    public Task<KmDeletionDeliveryResult> SubmitAsync(KmDocumentDeletionV2Request request,
        CancellationToken cancellationToken = default) => Task.FromResult(
            KmDeletionDeliveryResult.Failure(false, "V2_DELETION_CLIENT_UNAVAILABLE"));
}
