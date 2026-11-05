using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public static class KmAnalysisSourceTypes
{
    public const string TestReport = "TestReport";
    public const string DocumentLibraryFile = "DocumentLibraryFile";
    public const string VerificationApplicationAttachment = "VerificationApplicationAttachment";
    public const string VendorFile = "VendorFile";
    public const string EquipmentAttachment = "EquipmentAttachment";

    public static readonly string[] All =
        [TestReport, DocumentLibraryFile, VerificationApplicationAttachment, VendorFile, EquipmentAttachment];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record KmV2CallbackTimestamps(
    [property: JsonRequired] DateTimeOffset? StartedAt,
    [property: JsonRequired] DateTimeOffset? CompletedAt,
    [property: JsonRequired] DateTimeOffset? FailedAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record KmV2CallbackError(string Code, string Message, [property: JsonRequired] bool? Retryable);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record KmV2DocumentJobStatusCallback(
    string SchemaVersion, Guid CallbackId, Guid EventId, Guid CsitJobId, string KmJobId,
    string SourceSystem, string SourceType, Guid SourceEntityId, Guid DocumentId, int Version,
    string SourceSha256, string Status, [property: JsonRequired] KmV2CallbackTimestamps? Timestamps,
    [property: JsonRequired] JsonElement? Result, [property: JsonRequired] KmV2CallbackError? Error);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record KmV2AnalysisResultCallback(
    string SchemaVersion, Guid AnalysisResultId, Guid CallbackId, string SourceSystem,
    string SourceType, Guid SourceEntityId, Guid DocumentId, int Version, Guid CsitJobId,
    string KmJobId, string SourceSha256, string MetadataSchemaVersion, JsonElement Result,
    DateTimeOffset AnalyzedAt, Guid? SupersedesAnalysisResultId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record KmAnalysisReviewRequest(string Decision,
    [property: JsonRequired] JsonElement? CorrectionsJson, [property: JsonRequired] string? Reason);

public sealed record KmAnalysisResultListItem(
    Guid Id, Guid AnalysisResultId, string SourceType, Guid SourceEntityId, Guid DocumentId,
    int Version, string Status, string? Title, DateTime ReceivedAt, string ReviewStatus);

public sealed record KmAnalysisResultPage(
    IReadOnlyList<KmAnalysisResultListItem> Items, int Page, int PageSize, int TotalCount);

public sealed record KmAnalysisReviewRevisionDto(
    Guid Id, int Revision, string Decision, JsonElement? CorrectionsJson, string? Reason,
    string ReviewedBy, DateTime ReviewedAt);

public sealed record KmAnalysisResultDetail(
    Guid Id, Guid AnalysisResultId, Guid CallbackId, string SourceType, Guid SourceEntityId,
    Guid DocumentId, int Version, Guid EventId, Guid CsitJobId, string KmJobId,
    string SourceSha256, string MetadataSchemaVersion, JsonElement Result,
    DateTime AnalyzedAt, DateTime ReceivedAt, DateTime IndexedAt, int SourceRevision,
    Guid? SupersedesResultId, DateTime? SupersededAt, string ReviewStatus,
    string? ReviewedBy, DateTime? ReviewedAt, IReadOnlyList<KmAnalysisReviewRevisionDto> Reviews);

public sealed record KmValidatedMetadata(
    string MetadataSchemaVersion, DateTime IndexedAt, string? Title, string Json);

public static class KmAnalysisMetadataValidator
{
    private const int MaxResultCharacters = 1_000_000;
    private static readonly HashSet<string> DocumentTypes =
        ["Specification", "Manual", "TestReport", "SOP", "MeetingMinutes", "IssueAnalysis", "Presentation", "Other"];
    private static readonly HashSet<string> Methods = ["Text", "OCR", "Mixed", "Visual", "None"];
    private static readonly HashSet<string> ForbiddenKeys = new(StringComparer.OrdinalIgnoreCase)
        { "chunks", "chunkText", "vectors", "vector", "embedding", "embeddings", "extractedText", "fullText", "rawContent", "content" };

    public static bool TryValidate(JsonElement result, string expectedVersion,
        out KmValidatedMetadata? metadata)
    {
        metadata = null;
        if (result.ValueKind != JsonValueKind.Object || result.GetRawText().Length > MaxResultCharacters ||
            ContainsForbiddenContent(result) ||
            !String(result, "metadataSchemaVersion", out var schema) || schema != expectedVersion ||
            !Object(result, "document", out var document) || !ValidDocument(document) ||
            !Object(result, "technical", out var technical) || !ValidTechnical(technical) ||
            !Object(result, "extraction", out var extraction) || !ValidExtraction(extraction) ||
            !Object(result, "indexing", out var indexing) || !ValidIndexing(indexing, out var indexedAt) ||
            !Array(result, "warnings", out _) || !Object(result, "sourceSpecific", out _))
            return false;
        StringOrNull(document, "title", out var title);
        metadata = new KmValidatedMetadata(schema!, indexedAt, title, result.GetRawText());
        return true;
    }

    public static bool IsSafeCorrections(JsonElement? corrections)
    {
        if (!corrections.HasValue || corrections.Value.ValueKind == JsonValueKind.Null) return true;
        return corrections.Value.ValueKind == JsonValueKind.Object &&
            corrections.Value.GetRawText().Length <= MaxResultCharacters &&
            !ContainsForbiddenContent(corrections.Value);
    }

    private static bool ValidDocument(JsonElement value) =>
        StringOrNull(value, "title", out _) && String(value, "documentType", out var type) &&
        DocumentTypes.Contains(type!) && StringOrNull(value, "summary", out _) &&
        StringOrNull(value, "language", out _) && StringOrNull(value, "documentDate", out var date) &&
        (date is null || DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out _)) && StringArray(value, "authors") &&
        StringOrNull(value, "department", out _) && StringOrNull(value, "documentVersionLabel", out _) &&
        StringArray(value, "keywords") && StringArray(value, "tags");

    private static bool ValidTechnical(JsonElement value)
    {
        foreach (var name in new[] { "productNames", "modelNumbers", "vendorNames", "technologies",
                     "standards", "softwareVersions", "hardwareVersions", "projectNames", "testItems",
                     "issueKeys", "errorCodes" })
            if (!StringArray(value, name)) return false;
        if (!Array(value, "measurements", out var measurements)) return false;
        return measurements.EnumerateArray().All(x => x.ValueKind == JsonValueKind.Object &&
            String(x, "metric", out var metric) && !string.IsNullOrWhiteSpace(metric) &&
            String(x, "value", out var measurement) && !string.IsNullOrWhiteSpace(measurement) &&
            StringOrNull(x, "unit", out _) && StringOrNull(x, "context", out _));
    }

    private static bool ValidExtraction(JsonElement value) =>
        StringOrNull(value, "sourceFormat", out _) && String(value, "method", out var method) &&
        Methods.Contains(method!) && IntOrNull(value, "pageCount", out var pages) && pages >= 0 &&
        StringArray(value, "sheetNames") && Int(value, "extractedCharacterCount", out var chars) &&
        chars >= 0 && Bool(value, "ocrUsed");

    private static bool ValidIndexing(JsonElement value, out DateTime indexedAt)
    {
        indexedAt = default;
        return String(value, "status", out var status) && status == "Completed" &&
            String(value, "indexId", out var indexId) && !string.IsNullOrWhiteSpace(indexId) &&
            Int(value, "chunkCount", out var chunks) && chunks >= 0 &&
            String(value, "indexedAt", out var timestamp) &&
            DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var parsed) && parsed.Offset == TimeSpan.Zero &&
            (indexedAt = parsed.UtcDateTime) != default;
    }

    private static bool ContainsForbiddenContent(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
                if (ForbiddenKeys.Contains(property.Name) || ContainsForbiddenContent(property.Value)) return true;
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray())
                if (ContainsForbiddenContent(item)) return true;
        return false;
    }

    private static bool Object(JsonElement parent, string name, out JsonElement value) =>
        parent.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;
    private static bool Array(JsonElement parent, string name, out JsonElement value) =>
        parent.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Array;
    private static bool String(JsonElement parent, string name, out string? value)
    {
        value = null;
        return parent.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String &&
            (value = item.GetString()) is not null;
    }
    private static bool StringOrNull(JsonElement parent, string name, out string? value)
    {
        value = null;
        if (!parent.TryGetProperty(name, out var item)) return false;
        if (item.ValueKind == JsonValueKind.Null) return true;
        return item.ValueKind == JsonValueKind.String && (value = item.GetString()) is not null;
    }
    private static bool StringArray(JsonElement parent, string name) =>
        Array(parent, name, out var value) && value.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String);
    private static bool Int(JsonElement parent, string name, out int value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var item) && item.TryGetInt32(out value);
    }
    private static bool IntOrNull(JsonElement parent, string name, out int value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var item) &&
            (item.ValueKind == JsonValueKind.Null || item.TryGetInt32(out value));
    }
    private static bool Bool(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var item) && item.ValueKind is JsonValueKind.True or JsonValueKind.False;
}
