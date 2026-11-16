using System.Text.Json;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public sealed record EquipmentKmDocumentSummary(
    string? Title, string? Summary, string? DocumentType, string? Language, string? DocumentDate,
    IReadOnlyList<string> Keywords);

public sealed record EquipmentKmExtractionSummary(
    string? SourceFormat, string? Method, bool? OcrUsed, int? PageCount,
    int? ExtractedCharacterCount, IReadOnlyList<string> SheetNames);

public sealed record EquipmentKmTechnicalGroup(string Label, IReadOnlyList<string> Values);
public sealed record EquipmentKmSpecification(string Field, string Value, IReadOnlyList<string> Evidence);

public sealed record EquipmentKmMetadataView(
    EquipmentKmDocumentSummary Document,
    EquipmentKmExtractionSummary Extraction,
    IReadOnlyList<EquipmentKmTechnicalGroup> Technical,
    IReadOnlyList<EquipmentKmSpecification> Specifications,
    string? CapabilitySummary,
    IReadOnlyList<string> Limitations,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Projects only fields actually present in a validated KM v2 result. It deliberately does not
/// interpret the v2 payload as the legacy EquipmentMetadataSuggestion contract or infer values.
/// </summary>
public static class EquipmentKmMetadataProjection
{
    private static readonly (string Key, string Label)[] TechnicalArrays =
    [
        ("productNames", "產品"), ("modelNumbers", "型號"), ("vendorNames", "廠商"),
        ("technologies", "技術"), ("standards", "標準"), ("softwareVersions", "軟體版本"),
        ("hardwareVersions", "硬體版本"), ("projectNames", "專案"), ("testItems", "測試項目"),
        ("issueKeys", "問題編號"), ("errorCodes", "錯誤碼")
    ];

    public static EquipmentKmMetadataView Project(JsonElement result)
    {
        var document = Object(result, "document");
        var documentSummary = new EquipmentKmDocumentSummary(
            String(document, "title"), String(document, "summary"),
            String(document, "documentType"), String(document, "language"),
            String(document, "documentDate"), StringArray(document, "keywords"));

        var extraction = Object(result, "extraction");
        var extractionSummary = new EquipmentKmExtractionSummary(
            String(extraction, "sourceFormat"), String(extraction, "method"),
            Boolean(extraction, "ocrUsed"), Integer(extraction, "pageCount"),
            Integer(extraction, "extractedCharacterCount"), StringArray(extraction, "sheetNames"));

        var technical = new List<EquipmentKmTechnicalGroup>();
        var technicalJson = Object(result, "technical");
        foreach (var definition in TechnicalArrays)
        {
            var values = StringArray(technicalJson, definition.Key);
            if (values.Count > 0) technical.Add(new(definition.Label, values));
        }
        var measurements = Array(technicalJson, "measurements")
            .Select(Measurement).Where(x => x is not null).Cast<string>().ToList();
        if (measurements.Count > 0) technical.Add(new("量測資訊", measurements));

        var sourceSpecific = Object(result, "sourceSpecific");
        var specifications = Array(sourceSpecific, "specifications")
            .Select(Specification).Where(x => x is not null).Cast<EquipmentKmSpecification>().ToList();
        var limitations = StringArray(sourceSpecific, "limitations");
        var evidence = EvidenceItems(sourceSpecific.TryGetProperty("evidence", out var evidenceJson)
            ? evidenceJson : default);
        return new(documentSummary, extractionSummary, technical, specifications,
            String(sourceSpecific, "capabilitySummary"), limitations, evidence,
            StringArray(result, "warnings"));
    }

    private static EquipmentKmSpecification? Specification(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        var field = FirstString(item, "field", "name", "key", "label");
        var value = item.TryGetProperty("value", out var valueJson) ? Scalar(valueJson) : null;
        if (string.IsNullOrWhiteSpace(field) || string.IsNullOrWhiteSpace(value)) return null;
        var evidence = item.TryGetProperty("evidence", out var evidenceJson)
            ? EvidenceItems(evidenceJson) : [];
        return new(field, value, evidence);
    }

    private static string? Measurement(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        var metric = String(item, "metric");
        var value = String(item, "value");
        if (string.IsNullOrWhiteSpace(metric) || string.IsNullOrWhiteSpace(value)) return null;
        var unit = String(item, "unit");
        var context = String(item, "context");
        return $"{metric}：{value}{(string.IsNullOrWhiteSpace(unit) ? "" : $" {unit}")}" +
               (string.IsNullOrWhiteSpace(context) ? "" : $"（{context}）");
    }

    private static IReadOnlyList<string> EvidenceItems(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray().Select(Evidence)
                .Where(x => x is not null).Cast<string>().ToList();
        var single = Evidence(value);
        return single is null ? [] : [single];
    }

    private static string? Evidence(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.String) return NonBlank(item.GetString());
        if (item.ValueKind != JsonValueKind.Object) return null;
        var parts = new List<string>();
        Add(parts, "文件", FirstString(item, "sourceFileName", "fileName"));
        Add(parts, "頁碼", ScalarProperty(item, "page"));
        Add(parts, "段落", String(item, "section"));
        Add(parts, "證據", FirstString(item, "excerpt", "quote"));
        return parts.Count == 0 ? null : string.Join("；", parts);
    }

    private static void Add(List<string> parts, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) parts.Add($"{label}：{value}");
    }

    private static string? ScalarProperty(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item)
            ? Scalar(item) : null;

    private static string? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => NonBlank(value.GetString()),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
        _ => null
    };

    private static string? FirstString(JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            var result = String(value, name);
            if (!string.IsNullOrWhiteSpace(result)) return result;
        }
        return null;
    }

    private static JsonElement Object(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Object ? value : default;

    private static IEnumerable<JsonElement> Array(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [];

    private static IReadOnlyList<string> StringArray(JsonElement parent, string name) =>
        Array(parent, name).Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => NonBlank(x.GetString())).Where(x => x is not null).Cast<string>().ToList();

    private static string? String(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? NonBlank(value.GetString()) : null;

    private static bool? Boolean(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static int? Integer(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed) ? parsed : null;

    private static string? NonBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
