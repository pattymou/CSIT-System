using System.Text.Json.Serialization;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record KmMetadataAttributes(
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Keywords,
    string? DisplayName,
    IReadOnlyList<string> ModelNumbers,
    string? VendorReference);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record KmMetadataChangeRequest(
    string SchemaVersion, Guid ChangeId, string SourceSystem, string SourceType,
    Guid SourceEntityId, Guid DocumentId, int Version, int SourceRevision,
    DateTimeOffset OccurredAt, KmV2DocumentClassification Classification,
    KmMetadataAttributes Attributes, IReadOnlyList<string> ChangedFields);

public sealed record KmMetadataChangeAccepted(Guid ChangeId, string KmChangeJobId, string Status);

public sealed record KmMetadataChangeDeliveryResult(bool Accepted, bool Retryable, string? JobId,
    string? ErrorCode, string? ErrorMessage)
{
    public static KmMetadataChangeDeliveryResult Success(string jobId) =>
        new(true, false, jobId, null, null);
    public static KmMetadataChangeDeliveryResult Failure(bool retryable, string code, string message) =>
        new(false, retryable, null, code, message);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record KmMetadataChangeStatusCallback(
    string SchemaVersion, Guid CallbackId, Guid ChangeId, string KmChangeJobId,
    string SourceSystem, string Status, int AppliedRevision,
    [property: JsonRequired] KmV2CallbackTimestamps? Timestamps,
    [property: JsonRequired] KmV2CallbackError? Error);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record KmMetadataChangeQueueRequest(
    [property: JsonRequired] KmV2DocumentClassification? Classification,
    [property: JsonRequired] KmMetadataAttributes? Attributes);

public sealed record KmMetadataChangeQueued(
    Guid ChangeId, string SourceType, Guid SourceEntityId, Guid DocumentId, int Version,
    int SourceRevision, IReadOnlyList<string> ChangedFields, string Status);
