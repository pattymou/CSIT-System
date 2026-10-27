namespace SIT.DepartmentSystem.Web.Entities;

public static class ApprovalAuditActions
{
    public const string Submitted = "Submitted";
    public const string ApprovalBypassed = "ApprovalBypassed";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string CommitStarted = "CommitStarted";
    public const string Committed = "Committed";
    public const string CommitFailed = "CommitFailed";
    public const string CommitRetryRequested = "CommitRetryRequested";
    public const string Withdrawn = "Withdrawn";
    public const string Resubmitted = "Resubmitted";
    public const string SubmitterDismissed = "SubmitterDismissed";
    public const string VerificationContentEdited = "VerificationContentEdited";
    public const string VerificationWorkCreated = "VerificationWorkCreated";
    public const string CaseDeletionFrozen = "CaseDeletionFrozen";
    public const string CaseDeletionCleanupStarted = "CaseDeletionCleanupStarted";
    public const string CaseDeletionCleanupFailed = "CaseDeletionCleanupFailed";
    public const string CaseDeletionCompleted = "CaseDeletionCompleted";
}

public sealed class ApprovalAuditEvent
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid SubmissionId { get; private set; }
    public string SourceMenuCode { get; private set; } = string.Empty;
    public string SourceModuleCode { get; private set; } = string.Empty;
    public string Action { get; private set; } = string.Empty;
    public string ActorAccount { get; private set; } = string.Empty;
    public DateTime OccurredAt { get; private set; }
    public ApprovalStatus? FromStatus { get; private set; }
    public ApprovalStatus? ToStatus { get; private set; }
    public string? Reason { get; private set; }
    public int? AttemptNumber { get; private set; }
    public string? Details { get; private set; }

    private ApprovalAuditEvent() { }

    public static ApprovalAuditEvent Create(Guid submissionId, string menuCode, string moduleCode,
        string action, string actor, DateTime when, ApprovalStatus? from, ApprovalStatus? to,
        string? reason = null, int? attempt = null, string? details = null) => new()
    {
        SubmissionId = submissionId,
        SourceMenuCode = menuCode,
        SourceModuleCode = moduleCode,
        Action = action,
        ActorAccount = actor.Trim().ToLowerInvariant(),
        OccurredAt = when,
        FromStatus = from,
        ToStatus = to,
        Reason = action == ApprovalAuditActions.Rejected
            ? string.IsNullOrWhiteSpace(reason)
                ? throw new ArgumentException("Rejected audit requires a reason.", nameof(reason))
                : reason.Trim()
            : reason,
        AttemptNumber = attempt,
        Details = details
    };
}
