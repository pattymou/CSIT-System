namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public sealed record KmReference(Guid Id, string? Number, string? Name);
public sealed record KmDocumentMetadata(string FileName, string ContentType, long SizeBytes);

public sealed record KmIngestionRequest(
    Guid EventId, string EventType, string SourceSystem, Guid DocumentId, int Version,
    KmReference Project, KmReference? Case, KmReference? Task,
    KmDocumentMetadata Document, DateTime CommittedAt);

public sealed record KmIngestionResponse(
    string JobId, string SourceSystem, Guid DocumentId, int Version, string Status);

public sealed record KmCallbackError(string Code, string Message);
public sealed record KmCallbackTimestamps(
    DateTime? StartedAt, DateTime? CompletedAt, DateTime? FailedAt);
public sealed record KmStatusCallback(
    string JobId, string SourceSystem, Guid DocumentId, int Version, string Status,
    KmCallbackTimestamps? Timestamps, KmCallbackError? Error);

public sealed record KmDeliveryResult(
    bool Accepted, bool Retryable, string? JobId, string? ErrorCode, string? ErrorMessage)
{
    public static KmDeliveryResult Success(string jobId) => new(true, false, jobId, null, null);
    public static KmDeliveryResult Failure(bool retryable, string code, string message) =>
        new(false, retryable, null, code, message);
}

public static class KmV2ApprovalModes
{
    public const string Approved = "Approved";
    public const string ApprovalBypassed = "ApprovalBypassed";
}

public sealed record KmV2DocumentJobSource(
    string Type,
    Guid EntityId,
    string ModuleCode,
    Guid? ApplicationId,
    Guid? RecordId,
    Guid? VendorId,
    string? EquipmentId);

public sealed record KmV2DocumentApproval(
    string Mode,
    Guid ReferenceId,
    DateTime ApprovedAt);

public sealed record KmV2Document(
    Guid Id,
    int Version,
    string OriginalFileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    string DownloadUrl,
    DateTime ArchivedAt,
    KmV2DocumentApproval Approval);

public sealed record KmV2DocumentClassification(string? L1, string? L2, string? L3);

public sealed record KmV2Supersedes(Guid OldDocumentId, int OldVersion, string OldSha256);

public sealed record KmV2DocumentJobRequest(
    string SchemaVersion,
    Guid EventId,
    Guid CsitJobId,
    string EventType,
    string SourceSystem,
    KmV2DocumentJobSource Source,
    KmV2Document Document,
    KmV2DocumentClassification Classification,
    KmV2Supersedes? Supersedes,
    DateTime OccurredAt);

public sealed record KmV2DocumentJobAcceptedResponse(
    Guid EventId,
    Guid CsitJobId,
    string KmJobId,
    string Status);

public sealed record KmV2TestReportCorrelationResponse(
    string SchemaVersion,
    string SourceSystem,
    string SourceType,
    Guid SourceEntityId,
    Guid DocumentId,
    int Version,
    Guid EventId,
    Guid CsitJobId,
    string KmJobId,
    string SourceSha256,
    string Status);

public sealed record EquipmentMetadataEvidence(
    string SourceFileName, int? Page, string? Section, string? Excerpt);
public sealed record EquipmentTechnicalSpecificationSuggestion(
    string Field, string Value, EquipmentMetadataEvidence Evidence);
public sealed record EquipmentMetadataSuggestion(
    string MetadataSchemaVersion,
    IReadOnlyList<EquipmentTechnicalSpecificationSuggestion> TechnicalSpecifications,
    string? CapabilitySummary,
    IReadOnlyList<string> Limitations,
    IReadOnlyList<EquipmentMetadataEvidence> Evidence);
public sealed record EquipmentDocumentStatusCallback(
    string SchemaVersion, string JobId, string SourceSystem, Guid DocumentId, int Version,
    string Status, KmCallbackTimestamps? Timestamps, EquipmentMetadataSuggestion? Result,
    KmCallbackError? Error);

public sealed class EquipmentMetadataReviewRequest
{
    public string Status { get; set; } = string.Empty;
    public string? CorrectedMetadataJson { get; set; }
}
