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
                table.HasCheckConstraint("ck_equipment_aggregate_request_manifest", "length(manifest_hash) = 64");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.ApparatusId).HasColumnName("apparatus_id").HasMaxLength(30).IsRequired();
            entity.Property(x => x.ModuleCode).HasColumnName("module_code").HasMaxLength(50).IsRequired();
            entity.Property(x => x.ManifestHash).HasColumnName("manifest_hash").HasMaxLength(64).IsRequired();
            entity.Property(x => x.AnalysisVersion).HasColumnName("analysis_version").HasMaxLength(30).IsRequired();
            entity.Property(x => x.SourceDocumentsJson).HasColumnName("source_documents_json").HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.Status).HasColumnName("status").HasMaxLength(30).IsRequired();
            entity.Property(x => x.RequestedAt).HasColumnName("requested_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.CompletedAt).HasColumnName("completed_at");
            entity.Property(x => x.FailureCode).HasColumnName("failure_code").HasMaxLength(100);
            entity.Property(x => x.FailureMessage).HasColumnName("failure_message").HasMaxLength(1000);
            entity.HasIndex(x => new { x.ApparatusId, x.ModuleCode, x.ManifestHash, x.AnalysisVersion }).IsUnique();
            entity.HasIndex(x => new { x.Status, x.UpdatedAt });
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
