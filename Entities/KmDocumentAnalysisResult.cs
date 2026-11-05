namespace SIT.DepartmentSystem.Web.Entities;

public static class KmAnalysisReviewStatuses
{
    public const string PendingVerification = "PendingVerification";
    public const string Verified = "Verified";
    public const string Rejected = "Rejected";
}

public sealed class KmDocumentAnalysisResult
{
    public Guid Id { get; private set; }
    public Guid AnalysisResultId { get; private set; }
    public Guid CallbackId { get; private set; }
    public Guid OutboxId { get; private set; }
    public string SourceType { get; private set; } = string.Empty;
    public Guid SourceEntityId { get; private set; }
    public Guid DocumentId { get; private set; }
    public int Version { get; private set; }
    public Guid EventId { get; private set; }
    public Guid CsitJobId { get; private set; }
    public string KmJobId { get; private set; } = string.Empty;
    public string SourceSha256 { get; private set; } = string.Empty;
    public string MetadataSchemaVersion { get; private set; } = string.Empty;
    public string Status { get; private set; } = "Completed";
    public string ResultJson { get; private set; } = string.Empty;
    public DateTime AnalyzedAt { get; private set; }
    public DateTime ReceivedAt { get; private set; }
    public DateTime IndexedAt { get; private set; }
    public string ReviewStatus { get; private set; } = KmAnalysisReviewStatuses.PendingVerification;
    public string? ReviewedBy { get; private set; }
    public DateTime? ReviewedAt { get; private set; }
    public Guid? SupersedesResultId { get; private set; }
    public DateTime? SupersededAt { get; private set; }
    public int SourceRevision { get; private set; }
    public string CallbackPayloadSha256 { get; private set; } = string.Empty;

    public KmIntegrationOutbox Outbox { get; private set; } = null!;
    public KmDocumentAnalysisResult? SupersedesResult { get; private set; }
    public ICollection<KmAnalysisReviewRevision> ReviewRevisions { get; private set; } = [];

    private KmDocumentAnalysisResult() { }

    public static KmDocumentAnalysisResult Create(
        Guid analysisResultId, Guid callbackId, KmIntegrationOutbox outbox,
        string sourceType, Guid sourceEntityId, Guid documentId, int version,
        Guid csitJobId, string kmJobId, string sourceSha256,
        string metadataSchemaVersion, string resultJson, DateTime analyzedAt,
        DateTime indexedAt, DateTime receivedAt, int sourceRevision,
        string callbackPayloadSha256, Guid? supersedesResultId = null)
    {
        if (analysisResultId == Guid.Empty || callbackId == Guid.Empty || outbox.Id == Guid.Empty ||
            string.IsNullOrWhiteSpace(sourceType) || sourceEntityId == Guid.Empty ||
            documentId == Guid.Empty || version < 1 || csitJobId == Guid.Empty ||
            string.IsNullOrWhiteSpace(kmJobId) || !IsSha256(sourceSha256) ||
            string.IsNullOrWhiteSpace(metadataSchemaVersion) || string.IsNullOrWhiteSpace(resultJson) ||
            sourceRevision < 1 || !IsSha256(callbackPayloadSha256))
            throw new InvalidOperationException("Invalid KM analysis result identity.");

        return new KmDocumentAnalysisResult
        {
            Id = Guid.NewGuid(), AnalysisResultId = analysisResultId, CallbackId = callbackId,
            OutboxId = outbox.Id, SourceType = sourceType.Trim(), SourceEntityId = sourceEntityId,
            DocumentId = documentId, Version = version, EventId = outbox.EventId,
            CsitJobId = csitJobId, KmJobId = kmJobId.Trim(),
            SourceSha256 = sourceSha256.ToLowerInvariant(),
            MetadataSchemaVersion = metadataSchemaVersion.Trim(), ResultJson = resultJson,
            AnalyzedAt = analyzedAt, IndexedAt = indexedAt, ReceivedAt = receivedAt,
            SourceRevision = sourceRevision,
            CallbackPayloadSha256 = callbackPayloadSha256.ToLowerInvariant(),
            SupersedesResultId = supersedesResultId
        };
    }

    public void MarkSuperseded(DateTime now) => SupersededAt ??= now;

    public KmAnalysisReviewRevision Review(string decision, string? correctionsJson,
        string? reason, string reviewedBy, DateTime now)
    {
        if (decision is not (KmAnalysisReviewStatuses.Verified or KmAnalysisReviewStatuses.Rejected) ||
            string.IsNullOrWhiteSpace(reviewedBy))
            throw new InvalidOperationException("Invalid analysis review decision.");
        var revision = KmAnalysisReviewRevision.Create(Id, ReviewRevisions.Count + 1,
            decision, correctionsJson, reason, reviewedBy, now);
        ReviewStatus = decision;
        ReviewedBy = reviewedBy.Trim().ToLowerInvariant();
        ReviewedAt = now;
        ReviewRevisions.Add(revision);
        return revision;
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
}

public sealed class KmAnalysisCallbackReceipt
{
    public Guid CallbackId { get; set; }
    public Guid OutboxId { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string PayloadSha256 { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }
    public KmIntegrationOutbox Outbox { get; set; } = null!;
}

public sealed class KmAnalysisReviewRevision
{
    public Guid Id { get; private set; }
    public Guid AnalysisResultId { get; private set; }
    public int Revision { get; private set; }
    public string Decision { get; private set; } = string.Empty;
    public string? CorrectionsJson { get; private set; }
    public string? Reason { get; private set; }
    public string ReviewedBy { get; private set; } = string.Empty;
    public DateTime ReviewedAt { get; private set; }
    public KmDocumentAnalysisResult AnalysisResult { get; private set; } = null!;

    private KmAnalysisReviewRevision() { }

    internal static KmAnalysisReviewRevision Create(Guid resultId, int revision,
        string decision, string? correctionsJson, string? reason, string reviewedBy, DateTime now) => new()
    {
        Id = Guid.NewGuid(), AnalysisResultId = resultId, Revision = revision,
        Decision = decision, CorrectionsJson = Clean(correctionsJson, 1_000_000),
        Reason = Clean(reason, 2000), ReviewedBy = reviewedBy.Trim().ToLowerInvariant(),
        ReviewedAt = now
    };

    private static string? Clean(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= maxLength
            ? value.Trim() : throw new InvalidOperationException("Review value is too large.");
}
