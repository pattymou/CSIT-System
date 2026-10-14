using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Data;

public static class SupervisorApprovalModelConfiguration
{
    public static void ConfigureSupervisorApproval(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PendingSubmission>(entity =>
        {
            entity.ToTable("pending_submissions", table =>
            {
                table.HasCheckConstraint("ck_pending_submissions_status",
                    "status IN ('Pending', 'ApprovedPendingCommit', 'Committed', 'Rejected', 'CommitFailed', 'Withdrawn')");
                table.HasCheckConstraint("ck_pending_submissions_rejected_reason",
                    "status <> 'Rejected' OR (reviewer_account IS NOT NULL AND reviewed_at IS NOT NULL AND reject_reason IS NOT NULL AND length(btrim(reject_reason)) > 0)");
                table.HasCheckConstraint("ck_pending_submissions_committed_result",
                    "status <> 'Committed' OR (commit_result_key IS NOT NULL AND length(btrim(commit_result_key)) > 0)");
                table.HasCheckConstraint("ck_pending_submissions_team",
                    "submitted_team_option_id <> '00000000-0000-0000-0000-000000000000'::uuid AND length(btrim(submitted_team_code)) > 0");
                table.HasCheckConstraint("ck_pending_submissions_file_metadata",
                    "staging_storage_key IS NULL OR (original_file_name IS NOT NULL AND content_type IS NOT NULL AND file_size > 0 AND file_hash IS NOT NULL)");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.SubmissionKey).HasColumnName("submission_key").HasMaxLength(150).IsRequired();
            entity.Property(x => x.SourceMenuCode).HasColumnName("source_menu_code").HasMaxLength(100).IsRequired();
            entity.Property(x => x.SourceModuleCode).HasColumnName("source_module_code").HasMaxLength(100).IsRequired();
            entity.Property(x => x.SubmittedByAccount).HasColumnName("submitted_by_account").HasMaxLength(100).IsRequired();
            entity.Property(x => x.SubmittedAt).HasColumnName("submitted_at");
            entity.Property(x => x.SubmittedDepartment).HasColumnName("submitted_department").HasMaxLength(200).IsRequired();
            entity.Property(x => x.SubmittedTeamOptionId).HasColumnName("submitted_team_option_id");
            entity.Property(x => x.SubmittedTeamCode).HasColumnName("submitted_team_code").HasMaxLength(100).IsRequired();
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.Property(x => x.ReviewerAccount).HasColumnName("reviewer_account").HasMaxLength(100);
            entity.Property(x => x.ReviewedAt).HasColumnName("reviewed_at");
            entity.Property(x => x.RejectReason).HasColumnName("reject_reason").HasMaxLength(PendingSubmission.MaxRejectReasonLength);
            entity.Property(x => x.ContextType).HasColumnName("context_type").HasMaxLength(100).IsRequired();
            entity.Property(x => x.ContextId).HasColumnName("context_id").HasMaxLength(150).IsRequired();
            entity.Property(x => x.ContextDisplay).HasColumnName("context_display").HasMaxLength(500);
            entity.Property(x => x.OriginalFileName).HasColumnName("original_file_name").HasMaxLength(255);
            entity.Property(x => x.ContentType).HasColumnName("content_type").HasMaxLength(200);
            entity.Property(x => x.FileSize).HasColumnName("file_size");
            entity.Property(x => x.FileHash).HasColumnName("file_hash").HasMaxLength(64);
            entity.Property(x => x.StagingStorageKey).HasColumnName("staging_storage_key").HasMaxLength(250);
            entity.Property(x => x.ResubmittedAsSubmissionId).HasColumnName("resubmitted_as_submission_id");
            entity.Property(x => x.ResubmittedAt).HasColumnName("resubmitted_at");
            entity.Property(x => x.SubmitterDismissedAt).HasColumnName("submitter_dismissed_at");
            entity.Property(x => x.CommitAttemptCount).HasColumnName("commit_attempt_count");
            entity.Property(x => x.LastCommitAttemptAt).HasColumnName("last_commit_attempt_at");
            entity.Property(x => x.LastCommitError).HasColumnName("last_commit_error").HasMaxLength(500);
            entity.Property(x => x.CommitResultKey).HasColumnName("commit_result_key").HasMaxLength(250);
            entity.Property(x => x.CommitLeaseToken).HasColumnName("commit_lease_token");
            entity.Property(x => x.CommitLeaseExpiresAt).HasColumnName("commit_lease_expires_at");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(x => x.SubmissionKey).IsUnique();
            entity.HasIndex(x => x.Status);
            entity.HasIndex(x => new { x.SubmittedByAccount, x.Status });
            entity.HasIndex(x => new { x.SubmittedTeamOptionId, x.Status });
            entity.HasIndex(x => new { x.SourceMenuCode, x.Status });
            entity.HasIndex(x => x.SubmittedAt);
        });

        modelBuilder.Entity<ApprovalAuditEvent>(entity =>
        {
            // No FK: ApprovalBypassed records an operation ID without creating a PendingSubmission.
            entity.ToTable("approval_audit_events", table =>
                table.HasCheckConstraint("ck_approval_audit_rejected_reason",
                    "action <> 'Rejected' OR (reason IS NOT NULL AND length(btrim(reason)) > 0)"));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.SubmissionId).HasColumnName("submission_id");
            entity.Property(x => x.SourceMenuCode).HasColumnName("source_menu_code").HasMaxLength(100).IsRequired();
            entity.Property(x => x.SourceModuleCode).HasColumnName("source_module_code").HasMaxLength(100).IsRequired();
            entity.Property(x => x.Action).HasColumnName("action").HasMaxLength(50).IsRequired();
            entity.Property(x => x.ActorAccount).HasColumnName("actor_account").HasMaxLength(100).IsRequired();
            entity.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            entity.Property(x => x.FromStatus).HasColumnName("from_status").HasConversion<string>().HasMaxLength(30);
            entity.Property(x => x.ToStatus).HasColumnName("to_status").HasConversion<string>().HasMaxLength(30);
            entity.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(PendingSubmission.MaxRejectReasonLength);
            entity.Property(x => x.AttemptNumber).HasColumnName("attempt_number");
            entity.Property(x => x.Details).HasColumnName("details").HasMaxLength(1000);
            entity.HasIndex(x => new { x.SubmissionId, x.OccurredAt });
        });
    }
}
