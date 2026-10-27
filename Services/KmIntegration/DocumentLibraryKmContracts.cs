namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public sealed record DocumentLibraryClassificationLevel(Guid Id, string Name);

public sealed record DocumentLibraryClassification(
    DocumentLibraryClassificationLevel L1,
    DocumentLibraryClassificationLevel L2,
    DocumentLibraryClassificationLevel L3);

public sealed record DocumentLibraryKmFile(
    string OriginalFileName,
    string StandardFileName,
    string? ContentType,
    long FileSizeBytes,
    string Sha256,
    string DownloadUrl);

public sealed record DocumentLibraryIngestionRequest(
    string SchemaVersion,
    string SourceSystem,
    string EventType,
    Guid DocumentFileId,
    Guid CsitJobId,
    DateTime OccurredAt,
    DocumentLibraryKmFile File,
    DocumentLibraryClassification Classification);

public sealed record DocumentLibraryIngestionResponse(
    Guid CsitJobId,
    string KmJobId,
    string Status);

public sealed record DocumentLibraryCallbackTimestamps(
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? FailedAt);

public sealed record DocumentLibraryCallbackError(
    string Code,
    string Message,
    bool? Retryable);

public sealed record DocumentLibraryMeasurement(
    string Metric,
    string Value,
    string? Unit,
    string? Context);

public sealed record DocumentLibraryDocumentMetadata(
    string? Title,
    string DocumentType,
    string? Summary,
    string? Language,
    string? DocumentDate,
    string[] Authors,
    string? Department,
    string? DocumentVersionLabel,
    string[] Keywords,
    string[] Tags);

public sealed record DocumentLibraryTechnicalMetadata(
    string[] ProductNames,
    string[] ModelNumbers,
    string[] VendorNames,
    string[] Technologies,
    string[] Standards,
    string[] SoftwareVersions,
    string[] HardwareVersions,
    string[] ProjectNames,
    string[] TestItems,
    DocumentLibraryMeasurement[] Measurements,
    string[] IssueKeys,
    string[] ErrorCodes);

public sealed record DocumentLibraryExtractionMetadata(
    string? SourceFormat,
    string Method,
    int? PageCount,
    string[] SheetNames,
    int ExtractedCharacterCount,
    bool OcrUsed);

public sealed record DocumentLibraryIndexingMetadata(
    string Status,
    string IndexId,
    int ChunkCount,
    DateTimeOffset IndexedAt);

public sealed record DocumentLibraryCompletedResult(
    string MetadataSchemaVersion,
    DocumentLibraryDocumentMetadata Document,
    DocumentLibraryTechnicalMetadata Technical,
    DocumentLibraryExtractionMetadata Extraction,
    DocumentLibraryIndexingMetadata Indexing,
    string[] Warnings);

public sealed record DocumentLibraryStatusCallback(
    string SchemaVersion,
    Guid CallbackId,
    string SourceSystem,
    Guid DocumentFileId,
    Guid CsitJobId,
    string KmJobId,
    string Status,
    DocumentLibraryCallbackTimestamps? Timestamps,
    DocumentLibraryCompletedResult? Result,
    DocumentLibraryCallbackError? Error);
