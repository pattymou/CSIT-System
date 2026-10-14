namespace SIT.DepartmentSystem.Web.Entities;

public enum ApprovalStatus
{
    Pending,
    ApprovedPendingCommit,
    Committed,
    Rejected,
    CommitFailed,
    Withdrawn
}

public sealed record StagedFileMetadata(
    string OriginalFileName,
    string ContentType,
    long FileSize,
    string FileHash,
    string StorageKey);

public sealed class PendingSubmission
{
    public const int MaxRejectReasonLength = 2000;

    public Guid Id { get; private set; }
    public string SubmissionKey { get; private set; } = string.Empty;
    public string SourceMenuCode { get; private set; } = string.Empty;
    public string SourceModuleCode { get; private set; } = string.Empty;
    public string SubmittedByAccount { get; private set; } = string.Empty;
    public DateTime SubmittedAt { get; private set; }
    public string SubmittedDepartment { get; private set; } = string.Empty;
    public Guid SubmittedTeamOptionId { get; private set; }
    public string SubmittedTeamCode { get; private set; } = string.Empty;
    public ApprovalStatus Status { get; private set; } = ApprovalStatus.Pending;
    public string? ReviewerAccount { get; private set; }
    public DateTime? ReviewedAt { get; private set; }
    public string? RejectReason { get; private set; }
    public string ContextType { get; private set; } = string.Empty;
    public string ContextId { get; private set; } = string.Empty;
    public string? ContextDisplay { get; private set; }
    public string? OriginalFileName { get; private set; }
    public string? ContentType { get; private set; }
    public long? FileSize { get; private set; }
    public string? FileHash { get; private set; }
    public string? StagingStorageKey { get; private set; }
    public Guid? ResubmittedAsSubmissionId { get; private set; }
    public DateTime? ResubmittedAt { get; private set; }
    public DateTime? SubmitterDismissedAt { get; private set; }
    public int CommitAttemptCount { get; private set; }
    public DateTime? LastCommitAttemptAt { get; private set; }
    public string? LastCommitError { get; private set; }
    public string? CommitResultKey { get; private set; }
    public Guid? CommitLeaseToken { get; private set; }
    public DateTime? CommitLeaseExpiresAt { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    private PendingSubmission() { }

    public static PendingSubmission Create(
        Guid id, string submissionKey, string sourceMenuCode, string sourceModuleCode,
        string submittedByAccount, string submittedDepartment, Guid submittedTeamOptionId,
        string submittedTeamCode, string contextType, string contextId,
        string? contextDisplay, StagedFileMetadata? file, DateTime now)
    {
        if (id == Guid.Empty || submittedTeamOptionId == Guid.Empty || string.IsNullOrWhiteSpace(submittedTeamCode))
            throw new InvalidOperationException("此項目無法判定主管審核 Team，請先補齊 Team 設定。");
        if (file is not null && (file.FileSize <= 0 || string.IsNullOrWhiteSpace(file.StorageKey)
            || string.IsNullOrWhiteSpace(file.FileHash) || string.IsNullOrWhiteSpace(file.OriginalFileName)))
            throw new ArgumentException("Staging file metadata is incomplete.", nameof(file));

        return new PendingSubmission
        {
            Id = id,
            SubmissionKey = Required(submissionKey),
            SourceMenuCode = Required(sourceMenuCode),
            SourceModuleCode = Required(sourceModuleCode),
            SubmittedByAccount = Required(submittedByAccount).ToLowerInvariant(),
            SubmittedDepartment = Required(submittedDepartment),
            SubmittedTeamOptionId = submittedTeamOptionId,
            SubmittedTeamCode = submittedTeamCode.Trim(),
            ContextType = Required(contextType),
            ContextId = Required(contextId),
            ContextDisplay = contextDisplay?.Trim(),
            OriginalFileName = file?.OriginalFileName,
            ContentType = file?.ContentType,
            FileSize = file?.FileSize,
            FileHash = file?.FileHash,
            StagingStorageKey = file?.StorageKey,
            SubmittedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public void Approve(string reviewer, DateTime now)
    {
        EnsureStatus(ApprovalStatus.Pending);
        ReviewerAccount = Required(reviewer).ToLowerInvariant();
        ReviewedAt = now;
        Status = ApprovalStatus.ApprovedPendingCommit;
        UpdatedAt = now;
    }

    public void Reject(string reviewer, string? reason, DateTime now)
    {
        EnsureStatus(ApprovalStatus.Pending);
        var trimmed = Required(reason);
        if (trimmed.Length > MaxRejectReasonLength)
            throw new ArgumentException($"Reject Reason must be at most {MaxRejectReasonLength} characters.", nameof(reason));
        ReviewerAccount = Required(reviewer).ToLowerInvariant();
        ReviewedAt = now;
        RejectReason = trimmed;
        Status = ApprovalStatus.Rejected;
        UpdatedAt = now;
    }

    public void Withdraw(DateTime now)
    {
        EnsureStatus(ApprovalStatus.Pending);
        Status = ApprovalStatus.Withdrawn;
        UpdatedAt = now;
    }

    public void MarkResubmitted(Guid newSubmissionId, DateTime now)
    {
        EnsureStatus(ApprovalStatus.Rejected);
        if (ResubmittedAsSubmissionId.HasValue || SubmitterDismissedAt.HasValue || newSubmissionId == Guid.Empty)
            throw new InvalidOperationException("此退回項目已處理，請重新整理頁面。");
        ResubmittedAsSubmissionId = newSubmissionId;
        ResubmittedAt = now;
        UpdatedAt = now;
    }

    public void Dismiss(DateTime now)
    {
        EnsureStatus(ApprovalStatus.Rejected);
        if (ResubmittedAsSubmissionId.HasValue || SubmitterDismissedAt.HasValue)
            throw new InvalidOperationException("此退回項目已處理，請重新整理頁面。");
        SubmitterDismissedAt = now;
        UpdatedAt = now;
    }

    public void ClearStagingFile(DateTime now)
    {
        if (Status != ApprovalStatus.Withdrawn && SubmitterDismissedAt is null)
            throw new InvalidOperationException("只有已撤回或移除的提交可以清除暫存檔案。");
        StagingStorageKey = null;
        UpdatedAt = now;
    }

    public void StartCommit(Guid leaseToken, DateTime now, TimeSpan leaseDuration)
    {
        EnsureStatus(ApprovalStatus.ApprovedPendingCommit);
        if (leaseToken == Guid.Empty || leaseDuration <= TimeSpan.Zero)
            throw new ArgumentException("A valid commit lease is required.");
        if (CommitLeaseExpiresAt > now)
            throw new InvalidOperationException("This submission is already being committed.");
        CommitLeaseToken = leaseToken;
        CommitLeaseExpiresAt = now.Add(leaseDuration);
        CommitAttemptCount++;
        LastCommitAttemptAt = now;
        LastCommitError = null;
        UpdatedAt = now;
    }

    public void Commit(Guid leaseToken, string resultKey, DateTime now)
    {
        EnsureLease(leaseToken);
        Status = ApprovalStatus.Committed;
        CommitResultKey = Required(resultKey);
        ClearLease();
        UpdatedAt = now;
    }

    public void FailCommit(Guid leaseToken, string safeError, DateTime now)
    {
        EnsureLease(leaseToken);
        Status = ApprovalStatus.CommitFailed;
        LastCommitError = Required(safeError)[..Math.Min(safeError.Trim().Length, 500)];
        ClearLease();
        UpdatedAt = now;
    }

    public void Retry(DateTime now)
    {
        EnsureStatus(ApprovalStatus.CommitFailed);
        Status = ApprovalStatus.ApprovedPendingCommit;
        LastCommitError = null;
        UpdatedAt = now;
    }

    private void EnsureLease(Guid leaseToken)
    {
        EnsureStatus(ApprovalStatus.ApprovedPendingCommit);
        if (leaseToken == Guid.Empty || CommitLeaseToken != leaseToken)
            throw new InvalidOperationException("Commit lease does not match.");
    }

    private void EnsureStatus(ApprovalStatus expected)
    {
        if (Status != expected)
            throw new InvalidOperationException($"Cannot transition {Status}; expected {expected}.");
    }

    private void ClearLease()
    {
        CommitLeaseToken = null;
        CommitLeaseExpiresAt = null;
    }

    private static string Required(string? value) => string.IsNullOrWhiteSpace(value)
        ? throw new ArgumentException("A required approval value is blank.")
        : value.Trim();
}
