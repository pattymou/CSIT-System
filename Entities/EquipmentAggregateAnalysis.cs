namespace SIT.DepartmentSystem.Web.Entities;

public static class EquipmentAggregateAnalysisStatuses
{
    public const string Pending = "Pending";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Superseded = "Superseded";
}

public sealed class EquipmentAggregateAnalysisRequest
{
    public Guid Id { get; private set; }
    public string ApparatusId { get; private set; } = string.Empty;
    public string ModuleCode { get; private set; } = string.Empty;
    public string ManifestHash { get; private set; } = string.Empty;
    public string AnalysisVersion { get; private set; } = string.Empty;
    public string SourceDocumentsJson { get; private set; } = "[]";
    public string Status { get; private set; } = EquipmentAggregateAnalysisStatuses.Pending;
    public DateTime RequestedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public string? FailureCode { get; private set; }
    public string? FailureMessage { get; private set; }

    private EquipmentAggregateAnalysisRequest() { }

    public static EquipmentAggregateAnalysisRequest Create(Guid requestId, string apparatusId,
        string moduleCode, string manifestHash, string analysisVersion, string documentsJson, DateTime now)
    {
        if (requestId == Guid.Empty || string.IsNullOrWhiteSpace(apparatusId) ||
            string.IsNullOrWhiteSpace(moduleCode) || !IsSha256(manifestHash) ||
            string.IsNullOrWhiteSpace(analysisVersion) || string.IsNullOrWhiteSpace(documentsJson))
            throw new InvalidOperationException("Invalid equipment aggregate analysis request.");
        return new()
        {
            Id = requestId, ApparatusId = apparatusId.Trim(), ModuleCode = moduleCode.Trim().ToLowerInvariant(),
            ManifestHash = manifestHash.ToLowerInvariant(), AnalysisVersion = analysisVersion.Trim(),
            SourceDocumentsJson = documentsJson, RequestedAt = now, UpdatedAt = now
        };
    }

    public void Complete(DateTime now)
    {
        if (Status != EquipmentAggregateAnalysisStatuses.Pending)
            throw new InvalidOperationException("Only a pending aggregate request can complete.");
        Status = EquipmentAggregateAnalysisStatuses.Completed;
        CompletedAt = now;
        UpdatedAt = now;
    }

    public void Fail(string code, string? message, DateTime now)
    {
        if (Status != EquipmentAggregateAnalysisStatuses.Pending)
            throw new InvalidOperationException("Only a pending aggregate request can fail.");
        Status = EquipmentAggregateAnalysisStatuses.Failed;
        FailureCode = Clean(code, 100) ?? "unknown";
        FailureMessage = Clean(message, 1000);
        UpdatedAt = now;
    }

    public void Supersede(DateTime now)
    {
        if (Status != EquipmentAggregateAnalysisStatuses.Pending) return;
        Status = EquipmentAggregateAnalysisStatuses.Superseded;
        UpdatedAt = now;
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static string? Clean(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null :
        new string(value.Trim().Where(x => !char.IsControl(x)).Take(max).ToArray());
}

public sealed class EquipmentAggregateAnalysisSnapshot
{
    public Guid Id { get; private set; }
    public Guid RequestId { get; private set; }
    public string ApparatusId { get; private set; } = string.Empty;
    public string ModuleCode { get; private set; } = string.Empty;
    public string ManifestHash { get; private set; } = string.Empty;
    public string AnalysisVersion { get; private set; } = string.Empty;
    public string SourceDocumentsJson { get; private set; } = "[]";
    public string ResultJson { get; private set; } = "{}";
    public DateTime AnalyzedAt { get; private set; }
    public DateTime ReceivedAt { get; private set; }
    public string ReviewStatus { get; private set; } = KmAnalysisReviewStatuses.PendingVerification;
    public string? ReviewedBy { get; private set; }
    public DateTime? ReviewedAt { get; private set; }
    public string? ReviewNote { get; private set; }

    private EquipmentAggregateAnalysisSnapshot() { }

    public static EquipmentAggregateAnalysisSnapshot Create(EquipmentAggregateAnalysisRequest request,
        string resultJson, DateTime analyzedAt, DateTime receivedAt) => new()
    {
        Id = Guid.NewGuid(), RequestId = request.Id, ApparatusId = request.ApparatusId,
        ModuleCode = request.ModuleCode, ManifestHash = request.ManifestHash,
        AnalysisVersion = request.AnalysisVersion, SourceDocumentsJson = request.SourceDocumentsJson,
        ResultJson = resultJson, AnalyzedAt = analyzedAt, ReceivedAt = receivedAt
    };

    public void Review(string decision, string reviewedBy, string? note, DateTime now)
    {
        if (decision is not (KmAnalysisReviewStatuses.Verified or KmAnalysisReviewStatuses.Rejected) ||
            string.IsNullOrWhiteSpace(reviewedBy))
            throw new InvalidOperationException("Invalid aggregate analysis review.");
        ReviewStatus = decision;
        ReviewedBy = reviewedBy.Trim().ToLowerInvariant();
        ReviewedAt = now;
        ReviewNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(note.Trim().Length, 2000)];
    }
}

public sealed class EquipmentAggregateAnalysisCallbackReceipt
{
    public Guid CallbackId { get; set; }
    public Guid RequestId { get; set; }
    public string PayloadSha256 { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }
}
