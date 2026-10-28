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

public sealed record VendorKmReference(Guid Id, string Name);
public sealed record VendorKmFile(
    Guid Id, string OriginalFileName, string StoredFileName, string ContentType,
    long SizeBytes, string Sha256, string DownloadUrl);
public sealed record VendorFileIngestionRequest(
    string SchemaVersion, Guid EventId, string EventType, string SourceSystem,
    Guid DocumentId, int Version, DateTime CommittedAt,
    VendorKmReference Vendor, VendorKmFile File);

public sealed record EquipmentKmReference(
    string Id, string? ProductsId, string Name, string Kind, string? Brand,
    Guid? VendorId, string? VendorName, string? Model);
public sealed record EquipmentKmFile(
    Guid Id, string OriginalFileName, string StoredFileName, string ContentType,
    long SizeBytes, string Sha256, string DownloadUrl, string DocumentKind);
public sealed record EquipmentDocumentIngestionRequest(
    string SchemaVersion, Guid EventId, string EventType, string SourceSystem,
    Guid DocumentId, int Version, DateTime CommittedAt,
    EquipmentKmReference Equipment, EquipmentKmFile File);

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
