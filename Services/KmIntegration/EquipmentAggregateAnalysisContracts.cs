using System.Text.Json.Serialization;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public sealed record EquipmentAggregateSourceDocument(
    Guid FileId, int Version, string Sha256, string KmJobId, Guid AnalysisResultId,
    string FileName, DateTime AnalyzedAt);

public sealed record EquipmentAggregateAnalysisRequestContract(
    string SchemaVersion, Guid RequestId, string SourceSystem, string ModuleCode,
    string ApparatusId, string ManifestHash, string AnalysisVersion,
    IReadOnlyList<EquipmentAggregateSourceDocument> Documents, DateTimeOffset RequestedAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EquipmentAggregateSpecification(
    string Name, string Value, [property: JsonRequired] string? Unit,
    IReadOnlyList<string> EvidenceIds);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EquipmentAggregateCapability(
    string Name, [property: JsonRequired] string? Description, IReadOnlyList<string> EvidenceIds);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EquipmentAggregateConflict(
    string Field, string Description, IReadOnlyList<string> EvidenceIds);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EquipmentAggregateEvidence(
    string EvidenceId, Guid FileId, Guid AnalysisResultId, string Description,
    [property: JsonRequired] string? Location);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EquipmentAggregateAnalysisResult(
    [property: JsonRequired] string? Overview,
    IReadOnlyList<EquipmentAggregateSpecification> TechnicalSpecifications,
    IReadOnlyList<EquipmentAggregateCapability> TestCapabilities,
    IReadOnlyList<string> Limitations,
    IReadOnlyList<EquipmentAggregateConflict> Conflicts,
    IReadOnlyList<EquipmentAggregateEvidence> Evidence,
    IReadOnlyList<string> Warnings);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EquipmentAggregateAnalysisCallback(
    string SchemaVersion, Guid CallbackId, Guid RequestId, string ModuleCode,
    string ApparatusId, string ManifestHash, string AnalysisVersion, string Status,
    [property: JsonRequired] EquipmentAggregateAnalysisResult? Result,
    [property: JsonRequired] KmV2CallbackError? Error,
    [property: JsonRequired] DateTimeOffset? AnalyzedAt);

public sealed record EquipmentAggregateAnalysisView(
    string ApparatusId, string ModuleCode, bool FeatureEnabled, string ManifestHash,
    int EligibleDocumentCount, int PendingDocumentCount, string Status, bool IsStale,
    Guid? SnapshotId, DateTime? AnalyzedAt, string? ReviewStatus,
    EquipmentAggregateAnalysisResult? Result);

public sealed record EquipmentAggregateAnalysisReviewRequest(string Decision, string? Note);

public sealed class EquipmentAggregateAnalysisOptions
{
    public const string SectionName = "KmEquipmentAggregateAnalysis";
    public bool Enabled { get; set; }
    public string AnalysisVersion { get; set; } = "1.0";
}
