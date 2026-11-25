using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Data;

public static class EquipmentAggregateAnalysisModelConfiguration
{
    public static void ConfigureEquipmentAggregateAnalysis(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EquipmentAggregateAnalysisRequest>(entity =>
        {
            entity.ToTable("equipment_aggregate_analysis_requests", table =>
            {
                table.HasCheckConstraint("ck_equipment_aggregate_request_status",
                    "status IN ('Pending','Completed','Failed','Superseded')");
                table.HasCheckConstraint("ck_equipment_aggregate_dispatch_status",
                    "dispatch_status IN ('Pending','Dispatching','Accepted','RetryWaiting','Deferred','Failed','Superseded')");
                table.HasCheckConstraint("ck_equipment_aggregate_request_manifest", "length(manifest_hash) = 64");
                table.HasCheckConstraint("ck_equipment_aggregate_request_attempt", "attempt_number >= 1");
                table.HasCheckConstraint("ck_equipment_aggregate_dispatch_lease",
                    "(dispatch_status = 'Dispatching') = (dispatch_lease_token IS NOT NULL AND dispatch_lease_expires_at IS NOT NULL)");
                table.HasCheckConstraint("ck_equipment_aggregate_dispatch_job",
                    "dispatch_status <> 'Accepted' OR km_job_id IS NOT NULL");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.ApparatusId).HasColumnName("apparatus_id").HasMaxLength(30).IsRequired();
            entity.Property(x => x.ModuleCode).HasColumnName("module_code").HasMaxLength(50).IsRequired();
            entity.Property(x => x.ManifestHash).HasColumnName("manifest_hash").HasMaxLength(64).IsRequired();
            entity.Property(x => x.AnalysisVersion).HasColumnName("analysis_version").HasMaxLength(30).IsRequired();
            entity.Property(x => x.AttemptNumber).HasColumnName("attempt_number").HasDefaultValue(1);
            entity.Property(x => x.RequestedBy).HasColumnName("requested_by").HasMaxLength(100);
            entity.Property(x => x.SourceDocumentsJson).HasColumnName("source_documents_json").HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.Status).HasColumnName("status").HasMaxLength(30).IsRequired();
            entity.Property(x => x.RequestedAt).HasColumnName("requested_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.CompletedAt).HasColumnName("completed_at");
            entity.Property(x => x.FailureCode).HasColumnName("failure_code").HasMaxLength(100);
            entity.Property(x => x.FailureMessage).HasColumnName("failure_message").HasMaxLength(1000);
            entity.Property(x => x.DispatchStatus).HasColumnName("dispatch_status").HasMaxLength(30).IsRequired()
                .HasDefaultValue(EquipmentAggregateDispatchStatuses.Pending);
            entity.Property(x => x.KmJobId).HasColumnName("km_job_id").HasMaxLength(150);
            entity.Property(x => x.DispatchAttemptCount).HasColumnName("dispatch_attempt_count");
            entity.Property(x => x.DispatchNextRetryAt).HasColumnName("dispatch_next_retry_at");
            entity.Property(x => x.DispatchLastErrorCode).HasColumnName("dispatch_last_error_code").HasMaxLength(100);
            entity.Property(x => x.DispatchLastErrorMessage).HasColumnName("dispatch_last_error_message").HasMaxLength(1000);
            entity.Property(x => x.DispatchLastAttemptAt).HasColumnName("dispatch_last_attempt_at");
            entity.Property(x => x.DispatchLeaseToken).HasColumnName("dispatch_lease_token");
            entity.Property(x => x.DispatchLeaseExpiresAt).HasColumnName("dispatch_lease_expires_at");
            entity.Property(x => x.DispatchedAt).HasColumnName("dispatched_at");
            entity.HasIndex(x => new { x.ApparatusId, x.ModuleCode, x.ManifestHash, x.AnalysisVersion, x.AttemptNumber }).IsUnique();
            entity.HasIndex(x => new { x.Status, x.UpdatedAt });
            entity.HasIndex(x => new { x.Status, x.DispatchStatus, x.DispatchNextRetryAt });
            entity.HasIndex(x => x.DispatchLeaseExpiresAt);
            entity.HasIndex(x => x.KmJobId).IsUnique().HasFilter("km_job_id IS NOT NULL");
        });

        modelBuilder.Entity<EquipmentAggregateAnalysisSnapshot>(entity =>
        {
            entity.ToTable("equipment_aggregate_analysis_snapshots", table =>
            {
                table.HasCheckConstraint("ck_equipment_aggregate_snapshot_manifest", "length(manifest_hash) = 64");
                table.HasCheckConstraint("ck_equipment_aggregate_snapshot_review",
                    "review_status IN ('PendingVerification','Verified','Rejected')");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.RequestId).HasColumnName("request_id");
            entity.Property(x => x.ApparatusId).HasColumnName("apparatus_id").HasMaxLength(30).IsRequired();
            entity.Property(x => x.ModuleCode).HasColumnName("module_code").HasMaxLength(50).IsRequired();
            entity.Property(x => x.ManifestHash).HasColumnName("manifest_hash").HasMaxLength(64).IsRequired();
            entity.Property(x => x.AnalysisVersion).HasColumnName("analysis_version").HasMaxLength(30).IsRequired();
            entity.Property(x => x.SourceDocumentsJson).HasColumnName("source_documents_json").HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.ResultJson).HasColumnName("result_json").HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.AnalyzedAt).HasColumnName("analyzed_at");
            entity.Property(x => x.ReceivedAt).HasColumnName("received_at");
            entity.Property(x => x.ReviewStatus).HasColumnName("review_status").HasMaxLength(30).IsRequired();
            entity.Property(x => x.ReviewedBy).HasColumnName("reviewed_by").HasMaxLength(100);
            entity.Property(x => x.ReviewedAt).HasColumnName("reviewed_at");
            entity.Property(x => x.ReviewNote).HasColumnName("review_note").HasMaxLength(2000);
            entity.HasIndex(x => x.RequestId).IsUnique();
            entity.HasIndex(x => new { x.ApparatusId, x.ModuleCode, x.ReceivedAt });
            entity.HasOne<EquipmentAggregateAnalysisRequest>().WithOne().HasForeignKey<EquipmentAggregateAnalysisSnapshot>(x => x.RequestId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<EquipmentAggregateAnalysisCallbackReceipt>(entity =>
        {
            entity.ToTable("equipment_aggregate_analysis_callbacks");
            entity.HasKey(x => x.CallbackId);
            entity.Property(x => x.CallbackId).HasColumnName("callback_id");
            entity.Property(x => x.RequestId).HasColumnName("request_id");
            entity.Property(x => x.PayloadSha256).HasColumnName("payload_sha256").HasMaxLength(64).IsRequired();
            entity.Property(x => x.ReceivedAt).HasColumnName("received_at");
            entity.HasIndex(x => x.RequestId);
            entity.HasOne<EquipmentAggregateAnalysisRequest>().WithMany().HasForeignKey(x => x.RequestId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
