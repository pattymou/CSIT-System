using System.Text.Json;
using System.Text.RegularExpressions;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public static partial class EquipmentAggregateAnalysisPresentation
{
    public static bool IsBlank(EquipmentAggregateAnalysisView? view, bool? hasFiles) =>
        hasFiles == false && view is
        {
            Result: null,
            EligibleDocumentCount: 0,
            PendingDocumentCount: 0,
            SnapshotId: null,
            Status: "NoEligibleDocuments" or "FeatureDisabled"
        };

    public static string? ProblemTraceId(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("traceId", out var property) ||
                property.ValueKind != JsonValueKind.String)
                return null;
            var traceId = property.GetString();
            return traceId is not null && TraceIdPattern().IsMatch(traceId) ? traceId : null;
        }
        catch (JsonException) { return null; }
    }

    [GeneratedRegex("^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TraceIdPattern();
}
