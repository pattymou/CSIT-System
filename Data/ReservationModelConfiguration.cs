using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Data;

internal static class ReservationModelConfiguration
{
    public static void ConfigureReservations(this ModelBuilder modelBuilder)
    {
        modelBuilder.HasSequence<long>("reservation_no_seq");

        modelBuilder.Entity<Reservation>(entity =>
        {
            entity.ToTable("reservations", table =>
            {
                table.HasCheckConstraint(
                    "ck_reservations_status",
                    "status IN ('Draft', 'Pending', 'Approved', 'Borrowed', 'Returned', 'Rejected', 'Cancelled')");
                table.HasCheckConstraint("ck_reservations_time_range", "start_time < end_time");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.ReservationNo).HasColumnName("reservation_no").HasMaxLength(40).IsRequired();
            entity.Property(x => x.ApplicantAccount).HasColumnName("applicant_account").HasMaxLength(100).IsRequired();
            entity.Property(x => x.ApplicantName).HasColumnName("applicant_name").HasMaxLength(200).IsRequired();
            entity.Property(x => x.ApplicantDepartment).HasColumnName("applicant_department").HasMaxLength(200).IsRequired();
            entity.Property(x => x.ApplicantEmail).HasColumnName("applicant_email").HasMaxLength(320);
            entity.Property(x => x.ApplicantExtension).HasColumnName("applicant_extension").HasMaxLength(30);
            entity.Property(x => x.ApplicantAgentName).HasColumnName("applicant_agent_name").HasMaxLength(200);
            entity.Property(x => x.ApplicantAgentExtension).HasColumnName("applicant_agent_extension").HasMaxLength(30);
            entity.Property(x => x.ApplicantAgentEmail).HasColumnName("applicant_agent_email").HasMaxLength(320);
            entity.Property(x => x.Purpose).HasColumnName("purpose").IsRequired();
            entity.Property(x => x.ProductModelName).HasColumnName("product_model_name").HasMaxLength(300);
            entity.Property(x => x.Customer).HasColumnName("customer").HasMaxLength(200);
            entity.Property(x => x.ProjectSubPu).HasColumnName("project_sub_pu").HasMaxLength(200);
            entity.Property(x => x.Note).HasColumnName("note").HasMaxLength(2000);
            entity.Property(x => x.StartTime).HasColumnName("start_time");
            entity.Property(x => x.EndTime).HasColumnName("end_time");
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(x => x.TestExecutionProfileId).HasColumnName("test_execution_profile_id");
            entity.Property(x => x.TestEnvironmentId).HasColumnName("test_environment_id");
            entity.Property(x => x.EquipmentGroupId).HasColumnName("equipment_group_id");
            entity.Property(x => x.TestEnvironmentCodeSnapshot).HasColumnName("test_environment_code_snapshot").HasMaxLength(100);
            entity.Property(x => x.TestEnvironmentNameSnapshot).HasColumnName("test_environment_name_snapshot").HasMaxLength(200);
            entity.Property(x => x.EquipmentGroupCodeSnapshot).HasColumnName("equipment_group_code_snapshot").HasMaxLength(100);
            entity.Property(x => x.EquipmentGroupNameSnapshot).HasColumnName("equipment_group_name_snapshot").HasMaxLength(200);
            entity.Property(x => x.TestExecutionProfileCodeSnapshot).HasColumnName("test_execution_profile_code_snapshot").HasMaxLength(100);
            entity.Property(x => x.TestExecutionProfileNameSnapshot).HasColumnName("test_execution_profile_name_snapshot").HasMaxLength(200);
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.ApprovedAt).HasColumnName("approved_at");
            entity.Property(x => x.ApprovedBy).HasColumnName("approved_by").HasMaxLength(100);
            entity.Property(x => x.RejectedAt).HasColumnName("rejected_at");
            entity.Property(x => x.RejectedBy).HasColumnName("rejected_by").HasMaxLength(100);
            entity.Property(x => x.RejectReason).HasColumnName("reject_reason");
            entity.Property(x => x.CancelledAt).HasColumnName("cancelled_at");
            entity.Property(x => x.CancelledBy).HasColumnName("cancelled_by").HasMaxLength(100);
            entity.Property(x => x.CancelReason).HasColumnName("cancel_reason");
            entity.Property(x => x.BorrowedAt).HasColumnName("borrowed_at");
            entity.Property(x => x.BorrowedBy).HasColumnName("borrowed_by").HasMaxLength(100);
            entity.Property(x => x.ReturnedAt).HasColumnName("returned_at");
            entity.Property(x => x.ReturnedBy).HasColumnName("returned_by").HasMaxLength(100);
            entity.HasIndex(x => x.ReservationNo).IsUnique();
            entity.HasIndex(x => x.Status);
            entity.HasIndex(x => new { x.StartTime, x.EndTime });
            entity.HasIndex(x => new { x.Status, x.StartTime, x.EndTime });
            entity.HasIndex(x => new { x.ApplicantAccount, x.Status });
            entity.HasIndex(x => new { x.ApplicantDepartment, x.Status, x.StartTime, x.EndTime });
            entity.HasIndex(x => x.TestExecutionProfileId);
            entity.HasIndex(x => x.TestEnvironmentId);
            entity.HasIndex(x => x.EquipmentGroupId);
            entity.HasOne(x => x.TestExecutionProfile).WithMany().HasForeignKey(x => x.TestExecutionProfileId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.TestEnvironment).WithMany().HasForeignKey(x => x.TestEnvironmentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.EquipmentGroup).WithMany().HasForeignKey(x => x.EquipmentGroupId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ReservationExtensionRequest>(entity =>
        {
            entity.ToTable("reservation_extension_requests", table =>
            {
                table.HasCheckConstraint("ck_reservation_extension_requests_time", "current_end_time_snapshot < requested_end_time");
                table.HasCheckConstraint("ck_reservation_extension_requests_status", "status IN ('Pending', 'Approved', 'Rejected', 'Cancelled')");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.ReservationId).HasColumnName("reservation_id");
            entity.Property(x => x.CurrentEndTimeSnapshot).HasColumnName("current_end_time_snapshot");
            entity.Property(x => x.RequestedEndTime).HasColumnName("requested_end_time");
            entity.Property(x => x.RequestedByAccount).HasColumnName("requested_by_account").HasMaxLength(100).IsRequired();
            entity.Property(x => x.RequestedByName).HasColumnName("requested_by_name").HasMaxLength(200).IsRequired();
            entity.Property(x => x.RequestedAt).HasColumnName("requested_at");
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(x => x.ReviewedAt).HasColumnName("reviewed_at");
            entity.Property(x => x.ReviewedByAccount).HasColumnName("reviewed_by_account").HasMaxLength(100);
            entity.Property(x => x.ReviewedByName).HasColumnName("reviewed_by_name").HasMaxLength(200);
            entity.Property(x => x.RejectReason).HasColumnName("reject_reason");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(x => x.ReservationId);
            entity.HasIndex(x => new { x.Status, x.RequestedAt });
            entity.HasIndex(x => x.ReservationId).IsUnique().HasFilter("status = 'Pending'");
            entity.HasOne(x => x.Reservation).WithMany(x => x.ExtensionRequests)
                .HasForeignKey(x => x.ReservationId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReservationAuditEvent>(entity =>
        {
            entity.ToTable("reservation_audit_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.ReservationId).HasColumnName("reservation_id");
            entity.Property(x => x.Action).HasColumnName("action").HasMaxLength(50).IsRequired();
            entity.Property(x => x.FromStatus).HasColumnName("from_status").HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.ToStatus).HasColumnName("to_status").HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.ActorAccount).HasColumnName("actor_account").HasMaxLength(100).IsRequired();
            entity.Property(x => x.ActorName).HasColumnName("actor_name").HasMaxLength(200).IsRequired();
            entity.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            entity.Property(x => x.Reason).HasColumnName("reason");
            entity.Property(x => x.Details).HasColumnName("details");
            entity.HasIndex(x => new { x.ReservationId, x.OccurredAt });
            entity.HasOne(x => x.Reservation).WithMany(x => x.AuditEvents)
                .HasForeignKey(x => x.ReservationId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReservationItem>(entity =>
        {
            entity.ToTable("reservation_items");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.ReservationId).HasColumnName("reservation_id");
            entity.Property(x => x.ApparatusId).HasColumnName("apparatus_id").HasMaxLength(30).IsRequired();
            entity.Property(x => x.EquipmentGroupRequirementId).HasColumnName("equipment_group_requirement_id");
            entity.Property(x => x.RequirementResourceTypeSnapshot).HasColumnName("requirement_resource_type_snapshot").HasMaxLength(100);
            entity.Property(x => x.RequirementCapabilityTagSnapshot).HasColumnName("requirement_capability_tag_snapshot").HasMaxLength(200);
            entity.Property(x => x.ApparatusName).HasColumnName("apparatus_name").HasMaxLength(200).IsRequired();
            entity.Property(x => x.ProductsId).HasColumnName("products_id").HasMaxLength(100);
            entity.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(100);
            entity.Property(x => x.Brand).HasColumnName("brand");
            entity.Property(x => x.Model).HasColumnName("model");
            entity.Property(x => x.Number).HasColumnName("number");
            entity.Property(x => x.Place).HasColumnName("place");
            entity.Property(x => x.Custodian).HasColumnName("custodian").HasMaxLength(100);
            entity.Property(x => x.CustodianDepartment).HasColumnName("custodian_department");
            entity.Property(x => x.PriceUse).HasColumnName("price_use");
            entity.HasIndex(x => x.ReservationId);
            entity.HasIndex(x => x.ApparatusId);
            entity.HasIndex(x => x.EquipmentGroupRequirementId);
            entity.HasIndex(x => new { x.ReservationId, x.ApparatusId }).IsUnique();
            entity.HasOne(x => x.Reservation)
                .WithMany(x => x.Items)
                .HasForeignKey(x => x.ReservationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Apparatus)
                .WithMany()
                .HasForeignKey(x => x.ApparatusId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.EquipmentGroupRequirement)
                .WithMany()
                .HasForeignKey(x => x.EquipmentGroupRequirementId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
