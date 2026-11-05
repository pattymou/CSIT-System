using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Data;

public static class KmAnalysisModelConfiguration
{
    public static void ConfigureKmAnalysis(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<KmDocumentAnalysisResult>(entity =>
        {
            entity.ToTable("km_document_analysis_results", table =>
            {
                table.HasCheckConstraint("ck_km_analysis_version", "version >= 1");
                table.HasCheckConstraint("ck_km_analysis_status", "status = 'Completed'");
                table.HasCheckConstraint("ck_km_analysis_source_revision", "source_revision >= 1");
                table.HasCheckConstraint("ck_km_analysis_source_sha256", "length(source_sha256) = 64");
                table.HasCheckConstraint("ck_km_analysis_payload_sha256", "length(callback_payload_sha256) = 64");
                table.HasCheckConstraint("ck_km_analysis_source_type",
                    "source_type IN ('TestReport','DocumentLibraryFile','VerificationApplicationAttachment','VendorFile','EquipmentAttachment')");
                table.HasCheckConstraint("ck_km_analysis_schema_version",
                    "metadata_schema_version IN ('1.0','2.0')");
                table.HasCheckConstraint("ck_km_analysis_review_status",
                    "review_status IN ('PendingVerification','Verified','Rejected')");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.AnalysisResultId).HasColumnName("analysis_result_id");
            entity.Property(x => x.CallbackId).HasColumnName("callback_id");
            entity.Property(x => x.OutboxId).HasColumnName("outbox_id");
            entity.Property(x => x.SourceType).HasColumnName("source_type").HasMaxLength(80);
            entity.Property(x => x.SourceEntityId).HasColumnName("source_entity_id");
            entity.Property(x => x.DocumentId).HasColumnName("document_id");
            entity.Property(x => x.Version).HasColumnName("version");
            entity.Property(x => x.EventId).HasColumnName("event_id");
            entity.Property(x => x.CsitJobId).HasColumnName("csit_job_id");
            entity.Property(x => x.KmJobId).HasColumnName("km_job_id").HasMaxLength(150);
            entity.Property(x => x.SourceSha256).HasColumnName("source_sha256").HasColumnType("char(64)");
            entity.Property(x => x.MetadataSchemaVersion).HasColumnName("metadata_schema_version").HasMaxLength(12);
            entity.Property(x => x.Status).HasColumnName("status").HasMaxLength(30);
            entity.Property(x => x.ResultJson).HasColumnName("result_json").HasColumnType("jsonb");
            entity.Property(x => x.AnalyzedAt).HasColumnName("analyzed_at");
            entity.Property(x => x.ReceivedAt).HasColumnName("received_at");
            entity.Property(x => x.IndexedAt).HasColumnName("indexed_at");
            entity.Property(x => x.ReviewStatus).HasColumnName("review_status").HasMaxLength(30);
            entity.Property(x => x.ReviewedBy).HasColumnName("reviewed_by").HasMaxLength(100);
            entity.Property(x => x.ReviewedAt).HasColumnName("reviewed_at");
            entity.Property(x => x.SupersedesResultId).HasColumnName("supersedes_result_id");
            entity.Property(x => x.SupersededAt).HasColumnName("superseded_at");
            entity.Property(x => x.SourceRevision).HasColumnName("source_revision");
            entity.Property(x => x.CallbackPayloadSha256).HasColumnName("callback_payload_sha256").HasColumnType("char(64)");
            entity.HasIndex(x => x.AnalysisResultId).IsUnique();
            entity.HasIndex(x => x.CallbackId).IsUnique();
            entity.HasIndex(x => new { x.SourceType, x.SourceEntityId });
            entity.HasIndex(x => new { x.DocumentId, x.Version });
            entity.HasIndex(x => new { x.SourceType, x.SourceEntityId, x.DocumentId, x.Version, x.SourceRevision }).IsUnique();
            entity.HasOne(x => x.Outbox).WithMany().HasForeignKey(x => x.OutboxId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<KmAnalysisCallbackReceipt>().WithMany().HasForeignKey(x => x.CallbackId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.SupersedesResult).WithMany().HasForeignKey(x => x.SupersedesResultId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<KmAnalysisCallbackReceipt>(entity =>
        {
            entity.ToTable("km_analysis_callback_receipts", table =>
            {
                table.HasCheckConstraint("ck_km_analysis_callback_status",
                    "status IN ('Processing','Completed','Failed')");
                table.HasCheckConstraint("ck_km_analysis_callback_sha256", "length(payload_sha256) = 64");
            });
            entity.HasKey(x => x.CallbackId);
            entity.Property(x => x.CallbackId).HasColumnName("callback_id");
            entity.Property(x => x.OutboxId).HasColumnName("outbox_id");
            entity.Property(x => x.Endpoint).HasColumnName("endpoint").HasMaxLength(80);
            entity.Property(x => x.Status).HasColumnName("status").HasMaxLength(30);
            entity.Property(x => x.PayloadSha256).HasColumnName("payload_sha256").HasColumnType("char(64)");
            entity.Property(x => x.ReceivedAt).HasColumnName("received_at");
            entity.HasIndex(x => x.OutboxId);
            entity.HasOne(x => x.Outbox).WithMany().HasForeignKey(x => x.OutboxId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<KmAnalysisReviewRevision>(entity =>
        {
            entity.ToTable("km_analysis_review_revisions", table =>
            {
                table.HasCheckConstraint("ck_km_analysis_review_revision", "revision >= 1");
                table.HasCheckConstraint("ck_km_analysis_review_decision", "decision IN ('Verified','Rejected')");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.AnalysisResultId).HasColumnName("analysis_result_id");
            entity.Property(x => x.Revision).HasColumnName("revision");
            entity.Property(x => x.Decision).HasColumnName("decision").HasMaxLength(30);
            entity.Property(x => x.CorrectionsJson).HasColumnName("corrections_json").HasColumnType("jsonb");
            entity.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(2000);
            entity.Property(x => x.ReviewedBy).HasColumnName("reviewed_by").HasMaxLength(100);
            entity.Property(x => x.ReviewedAt).HasColumnName("reviewed_at");
            entity.HasIndex(x => new { x.AnalysisResultId, x.Revision }).IsUnique();
            entity.HasOne(x => x.AnalysisResult).WithMany(x => x.ReviewRevisions)
                .HasForeignKey(x => x.AnalysisResultId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
