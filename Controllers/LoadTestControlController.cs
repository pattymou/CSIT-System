using System.Data.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Models;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Approval;
using SIT.DepartmentSystem.Web.Services.Interfaces;
using SIT.DepartmentSystem.Web.Services.LoadTesting;

namespace SIT.DepartmentSystem.Web.Controllers;

/// <summary>
/// A loopback-only control plane for data that has no production HTTP workflow (for example,
/// Blazor supervisor review). LoadTestSafety makes every action fail closed outside an isolated run.
/// </summary>
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("api/load-test-control")]
[Authorize(Policy = SystemAuthorization.Policies.CsitStaff)]
public sealed class LoadTestControlController(
    IDbContextFactory<AppDbContext> dbFactory,
    IConfiguration configuration,
    IVerificationApplicationService applications,
    ApprovalSubmissionService submissions,
    ApprovalCommitWorker commits) : ControllerBase
{
    [HttpGet("health")]
    public IActionResult Health()
    {
        Demand();
        return Ok(new { isolated = true, runId = configuration["LoadTest:RunId"],
            database = $"csit_load_test_{configuration["LoadTest:RunId"]}", kmDispatch = false });
    }

    [HttpPost("bootstrap")]
    public async Task<IActionResult> Bootstrap(CancellationToken cancellationToken)
    {
        Demand();
        if (!User.IsInRole("Admin")) return Forbid();
        var runId = configuration["LoadTest:RunId"]!;
        var root = configuration["LoadTest:StorageRoot"]!;
        var now = DateTime.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var department = await EnsureOptionAsync(db, SystemOptionCategories.Department, "LOAD-DEPT",
            "DA40", 9000, now, cancellationToken);
        var team = await EnsureOptionAsync(db, SystemOptionCategories.Team, "LOADTEAM",
            "LOADTEAM", 9001, now, cancellationToken);
        await EnsureOptionAsync(db, SystemOptionCategories.Customer, "Synthetic",
            "Synthetic", 9002, now, cancellationToken);
        await EnsureOptionAsync(db, "AssetRootPath", "equipment", Path.Combine(root, "assets"),
            9003, now, cancellationToken);
        await EnsureOptionAsync(db, "ApprovalStagingRoot", "default", Path.Combine(root, "approval-staging"),
            9004, now, cancellationToken);
        var leaderAccount = $"load-{runId}-teamleader-1";
        if (!await db.Modules.AnyAsync(x =>
                x.Code == VerificationApplicationWorkflow.ModuleCode, cancellationToken))
            db.Modules.Add(new ModuleEntity
            {
                Id = StableGuid(runId + ":verification-module"),
                Code = VerificationApplicationWorkflow.ModuleCode,
                Name = "Verification",
                DisplayName = "Load Test Verification",
                RoutePrefix = "/modules/verification",
                Description = "CSIT isolated load test",
                IsEnabled = true,
                SortOrder = 9000,
                CreatedAt = now,
                UpdatedAt = now
            });
        if (!await db.TeamRoutings.AnyAsync(x => x.TeamOptionId == team.Id, cancellationToken))
            db.TeamRoutings.Add(new TeamRouting { Id = StableGuid(runId + ":routing"), TeamOptionId = team.Id,
                LeaderAccount = leaderAccount, LeaderDisplayName = "Load Team Leader", IsEnabled = true,
                CreatedAt = now, UpdatedAt = now });
        for (var index = 1; index <= 50; index++)
        {
            foreach (var role in new[] { "employee", "readonly", "teamleader", "admin" })
            {
                var account = $"load-{runId}-{role}-{index}";
                if (!await db.Users.AnyAsync(x => x.Account == account, cancellationToken))
                    db.Users.Add(new AppUser { Id = StableGuid(account + ":user"), IdentityUserId = StableGuid(account),
                        Account = account, DisplayName = $"Load {role} {index}", Department = "DA40",
                        Email = $"{account}@load.invalid", IsAdmin = role == "admin" });
            }
        }
        if (!await db.MenuItems.AnyAsync(x => x.ModuleCode == "equipment", cancellationToken))
        {
            var sectionId = await db.MenuSections.OrderBy(x => x.SortOrder).Select(x => x.Id)
                .FirstAsync(cancellationToken);
            db.MenuItems.Add(new MenuItem { Id = StableGuid(runId + ":equipment-menu"), SectionId = sectionId,
                Code = "equipment", Title = "Load Test Equipment", RoutePath = "/load-test/equipment",
                SortOrder = 9900, IsEnabled = true, RequiresSupervisorApproval = true,
                ModuleCode = "equipment", CreatedAt = now, UpdatedAt = now });
        }

        var vendorId = StableGuid(runId + ":vendor");
        if (!await db.Vendors.AnyAsync(x => x.Id == vendorId, cancellationToken))
            db.Vendors.Add(new Vendor { Id = vendorId, Name = $"LOAD-{runId}-Vendor",
                NormalizedName = $"load-{runId}-vendor", Types = ["Manufacturer"], IsActive = true,
                StorageFolderName = $"LOAD-{runId}-Vendor", CreatedBy = "load-bootstrap", UpdatedBy = "load-bootstrap",
                CreatedAt = now, UpdatedAt = now });
        var equipmentId = $"LT-{runId}";
        if (!await db.Apparatuses.AnyAsync(x => x.Id == equipmentId, cancellationToken))
            db.Apparatuses.Add(new Apparatus { Id = equipmentId, ModuleCode = "equipment",
                ProductsId = $"ASSET-{runId}", Name = $"Load Equipment {runId}", Kind = "LoadTest", VendorId = vendorId,
                StorageFolderName = equipmentId, DepartmentOptionId = department.Id, OwnerTeamOptionId = team.Id,
                CustodianAccount = leaderAccount, ReservationStatus = "可借用", CreatedAt = now, UpdatedAt = now });

        var folder1 = await EnsureFolderAsync(db, StableGuid(runId + ":folder1"), null, 1,
            $"LOAD-{runId}", now, cancellationToken);
        var folder2 = await EnsureFolderAsync(db, StableGuid(runId + ":folder2"), folder1.Id, 2,
            "Concurrency", now, cancellationToken);
        var folder3 = await EnsureFolderAsync(db, StableGuid(runId + ":folder3"), folder2.Id, 3,
            "Artifacts", now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Ok(new { vendorId, equipmentId, folderId = folder3.Id,
            departmentId = department.Id, teamId = team.Id, leaderAccount });
    }

    [HttpPost("verification-applications/drafts")]
    public async Task<IActionResult> CreateVerificationDraft(CancellationToken cancellationToken)
    {
        Demand();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var department = await db.SystemOptions.AsNoTracking().SingleAsync(x =>
            x.Category == SystemOptionCategories.Department && x.Name == "LOAD-DEPT", cancellationToken);
        var team = await db.SystemOptions.AsNoTracking().SingleAsync(x =>
            x.Category == SystemOptionCategories.Team && x.Value == "LOADTEAM", cancellationToken);
        var draft = await applications.CreateDraftAsync(User, new CreateVerificationApplicationRequest
        {
            RoutingDepartmentOptionId = department.Id, TeamOptionId = team.Id,
            ProjectName = $"LOAD-{configuration["LoadTest:RunId"]}-{Guid.NewGuid():N}",
            SubPu = "1A", Customer = "Synthetic", ProductModel = "LOAD-MODEL",
            RequestedFinishDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)),
            ValidationRequirement = "Isolated concurrency validation"
        }, cancellationToken);
        return Ok(new { draft.Id, draft.Status, draft.ApplicationNo });
    }

    [HttpPost("verification-applications/{id:guid}/submit")]
    public async Task<IActionResult> SubmitVerification(Guid id, CancellationToken cancellationToken)
    {
        Demand();
        var direct = await applications.IsDirectApprovalApplicantAsync(User, cancellationToken);
        DateOnly? expectedEnd = direct ? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)) : null;
        var submitted = await applications.SubmitAsync(id, User, expectedEnd, cancellationToken);
        return Ok(new { submitted.Id, submitted.Status, submitted.ApplicationNo,
            accepted = submitted.Status == VerificationApplicationStatus.Accepted });
    }

    [HttpPost("verification-applications/{id:guid}/accept")]
    public async Task<IActionResult> AcceptVerification(Guid id, CancellationToken cancellationToken)
    {
        Demand();
        try
        {
            var accepted = await applications.AcceptAsync(id, User,
                DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)), cancellationToken);
            return Ok(new { accepted.Id, accepted.Status, accepted.ApplicationNo });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("approvals/{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, CancellationToken cancellationToken)
    {
        Demand();
        await submissions.ApproveAsync(id, User, cancellationToken);
        return NoContent();
    }

    [HttpPost("approvals/{id:guid}/commit")]
    public async Task<IActionResult> Commit(Guid id, CancellationToken cancellationToken)
    {
        Demand();
        await commits.ProcessAsync(id, cancellationToken);
        return NoContent();
    }

    [HttpPost("approvals/approve-next")]
    public async Task<IActionResult> ApproveNext([FromQuery] string contextId,
        CancellationToken cancellationToken)
    {
        Demand();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var id = await db.PendingSubmissions.AsNoTracking()
            .Where(x => x.ContextId == contextId && x.Status == ApprovalStatus.Pending)
            .OrderBy(x => x.SubmittedAt).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(cancellationToken);
        if (!id.HasValue) return NotFound();
        // Two reviewers intentionally race on the same attachment. The row lock must permit
        // exactly one approval transition.
        async Task<bool> CompetingApprovalAsync()
        {
            try { await submissions.ApproveAsync(id.Value, User, cancellationToken); return true; }
            catch (InvalidOperationException) { return false; }
        }
        var approvals = await Task.WhenAll(CompetingApprovalAsync(), CompetingApprovalAsync());
        var approvalWins = approvals.Count(x => x);
        if (approvalWins > 1)
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { error = "Concurrent review approved the same attachment more than once." });
        if (approvalWins == 0)
        {
            await using var raced = await dbFactory.CreateDbContextAsync(cancellationToken);
            var racedStatus = await raced.PendingSubmissions.AsNoTracking()
                .Where(x => x.Id == id.Value).Select(x => x.Status).SingleAsync(cancellationToken);
            if (racedStatus == ApprovalStatus.Pending)
                return StatusCode(StatusCodes.Status500InternalServerError,
                    new { error = "Concurrent review made no progress." });
            return Conflict(new { error = "Another request won the approval race.", racedStatus });
        }

        // Two workers then race on the approved item. The commit lease and idempotent formal
        // write must leave exactly one committed row.
        async Task CompetingCommitAsync()
        {
            try { await commits.ProcessAsync(id.Value, cancellationToken); }
            catch (InvalidOperationException) { /* Expected losing lease contender. */ }
        }
        await Task.WhenAll(CompetingCommitAsync(), CompetingCommitAsync());
        await using var check = await dbFactory.CreateDbContextAsync(cancellationToken);
        var committed = await check.PendingSubmissions.AsNoTracking()
            .SingleAsync(x => x.Id == id.Value, cancellationToken);
        if (committed.Status != ApprovalStatus.Committed ||
            !Guid.TryParse(committed.CommitResultKey, out var fileId) ||
            !await check.ApparatusFiles.AsNoTracking().AnyAsync(x => x.Id == fileId, cancellationToken))
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { error = "Approved attachment did not become one formal file.", committed.Status });
        return Ok(new { submissionId = id.Value, fileId, committed.Status,
            commitAttempts = committed.CommitAttemptCount });
    }

    [HttpGet("invariants")]
    public async Task<IActionResult> Invariants(CancellationToken cancellationToken)
    {
        Demand();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        var pgConnections = await ScalarLongAsync(connection,
            "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database()", cancellationToken);
        var deadlocks = await ScalarLongAsync(connection,
            "SELECT deadlocks FROM pg_stat_database WHERE datname = current_database()", cancellationToken);
        var duplicateApprovalCommits = await ScalarLongAsync(connection, """
            SELECT count(*) FROM (SELECT approval_operation_id FROM vendor_files
              WHERE approval_operation_id IS NOT NULL GROUP BY approval_operation_id HAVING count(*) > 1) d
            """, cancellationToken);
        var orphanVendorFiles = await ScalarLongAsync(connection, """
            SELECT count(*) FROM vendor_files f LEFT JOIN vendors v ON v.id=f.vendor_id WHERE v.id IS NULL
            """, cancellationToken);
        var orphanEquipmentFiles = await ScalarLongAsync(connection, """
            SELECT count(*) FROM apparatus_files f LEFT JOIN apparatus a ON a."Id"=f."ApparatusId" WHERE a."Id" IS NULL
            """, cancellationToken);
        var duplicateOutbox = await ScalarLongAsync(connection, """
            SELECT count(*) FROM (SELECT event_type, source_entity_id, version FROM km_integration_outbox
              GROUP BY event_type, source_entity_id, version HAVING count(*) > 1) d
            """, cancellationToken);
        var activeLeasesWithoutToken = await ScalarLongAsync(connection, """
            SELECT count(*) FROM km_integration_outbox WHERE status='Dispatching'
              AND (lease_token IS NULL OR lease_expires_at IS NULL)
            """, cancellationToken);
        var approvalLeasesWithoutToken = await ScalarLongAsync(connection, """
            SELECT count(*) FROM pending_submissions WHERE status='ApprovedPendingCommit'
              AND (commit_lease_token IS NULL OR commit_lease_expires_at IS NULL)
            """, cancellationToken);
        var deletionLeasesWithoutToken = await ScalarLongAsync(connection, """
            SELECT count(*) FROM vendor_file_deletion_jobs WHERE status='Dispatching'
              AND (lease_token IS NULL OR lease_expires_at IS NULL)
            """, cancellationToken);
        var duplicateFormalPaths = await ScalarLongAsync(connection, """
            SELECT count(*) FROM (
              SELECT "FilePath", "FileName" FROM apparatus_files
              GROUP BY "FilePath", "FileName" HAVING count(*) > 1
            ) d
            """, cancellationToken);
        var commitFailures = await db.PendingSubmissions.CountAsync(x =>
            x.Status == ApprovalStatus.CommitFailed, cancellationToken);
        var committedApprovals = await db.PendingSubmissions.CountAsync(x =>
            x.Status == ApprovalStatus.Committed, cancellationToken);
        var approvalLeaseAttempts = await db.PendingSubmissions.SumAsync(x =>
            (long)x.CommitAttemptCount, cancellationToken);
        var outboxEvents = await db.KmIntegrationOutbox.CountAsync(cancellationToken);
        var deletionHolds = await db.VendorFileDeletionJobs.CountAsync(cancellationToken);
        var documentFiles = await db.DocumentLibraryFiles.CountAsync(cancellationToken);
        var formalVendorFiles = await db.VendorFiles.CountAsync(cancellationToken);
        var formalEquipmentFiles = await db.ApparatusFiles.CountAsync(cancellationToken);
        var archivedVerificationAttachments = await db.VerificationApplicationFiles.CountAsync(x =>
            x.DocumentLibraryFileId != null, cancellationToken);
        var acceptedVerificationAttachments = await db.VerificationApplicationFiles.CountAsync(x =>
            x.VerificationApplication.Status == VerificationApplicationStatus.Accepted, cancellationToken);
        var unarchivedAcceptedVerificationAttachments = await db.VerificationApplicationFiles.CountAsync(x =>
            x.VerificationApplication.Status == VerificationApplicationStatus.Accepted &&
            x.DocumentLibraryFileId == null, cancellationToken);
        var acceptedVerificationApplications = await db.VerificationApplications.CountAsync(x =>
            x.Status == VerificationApplicationStatus.Accepted, cancellationToken);
        var approvedReservations = await db.Reservations.CountAsync(x =>
            x.Status == ReservationStatus.Approved, cancellationToken);
        var workload = configuration["LoadTest:Workload"] ?? "mixed";
        var requireDocuments = workload is "mixed" or "documents";
        var requireVerification = workload is "mixed" or "concurrency";
        var requireReservations = workload is "mixed" or "reservations" or "concurrency";
        var missingRequiredEvidence =
            requireDocuments && (committedApprovals == 0 || approvalLeaseAttempts == 0 ||
                outboxEvents == 0 || deletionHolds == 0 || documentFiles == 0 ||
                formalVendorFiles == 0 || formalEquipmentFiles == 0) ||
            requireVerification && (acceptedVerificationApplications == 0 ||
                acceptedVerificationAttachments == 0 ||
                unarchivedAcceptedVerificationAttachments != 0) ||
            requireReservations && approvedReservations == 0;
        var failed = duplicateApprovalCommits + orphanVendorFiles + orphanEquipmentFiles +
            duplicateOutbox + activeLeasesWithoutToken + approvalLeasesWithoutToken +
            deletionLeasesWithoutToken + duplicateFormalPaths + commitFailures +
            (missingRequiredEvidence ? 1 : 0);
        return Ok(new { passed = failed == 0, pgConnections, deadlocks, duplicateApprovalCommits,
            orphanVendorFiles, orphanEquipmentFiles, duplicateOutbox, activeLeasesWithoutToken,
            approvalLeasesWithoutToken, deletionLeasesWithoutToken, duplicateFormalPaths,
            commitFailures, committedApprovals, approvalLeaseAttempts, outboxEvents, deletionHolds,
            documentFiles, archivedVerificationAttachments, acceptedVerificationAttachments,
            unarchivedAcceptedVerificationAttachments, acceptedVerificationApplications,
            approvedReservations, missingRequiredEvidence,
            pendingSubmissions = await db.PendingSubmissions.CountAsync(cancellationToken),
            formalVendorFiles, formalEquipmentFiles,
            reservations = await db.Reservations.CountAsync(cancellationToken),
            verificationApplications = await db.VerificationApplications.CountAsync(cancellationToken) });
    }

    private void Demand() => LoadTestSafety.DemandRequest(HttpContext, configuration);

    private static async Task<SystemOption> EnsureOptionAsync(AppDbContext db, string category,
        string name, string value, int sort, DateTime now, CancellationToken token)
    {
        var item = await db.SystemOptions.SingleOrDefaultAsync(x => x.Category == category && x.Name == name, token);
        if (item is not null) { item.Value = value; item.IsEnabled = true; item.UpdatedAt = now; return item; }
        item = new SystemOption { Id = StableGuid($"{category}:{name}"), Category = category, Name = name,
            Value = value, Sort = sort, IsEnabled = true, Note = "CSIT isolated load test", CreatedAt = now, UpdatedAt = now };
        db.SystemOptions.Add(item); return item;
    }
    private static async Task<DocumentFolder> EnsureFolderAsync(AppDbContext db, Guid id, Guid? parent,
        int level, string name, DateTime now, CancellationToken token)
    {
        var item = await db.DocumentFolders.SingleOrDefaultAsync(x => x.Id == id, token);
        if (item is not null) return item;
        item = new DocumentFolder { Id = id, ParentId = parent, Level = level, Name = name,
            Description = "Synthetic load-test folder", SortOrder = 9000 + level, CreatedAt = now, UpdatedAt = now };
        db.DocumentFolders.Add(item); return item;
    }
    private static Guid StableGuid(string value) => new(System.Security.Cryptography.MD5.HashData(
        System.Text.Encoding.UTF8.GetBytes(value)));
    private static async Task<long> ScalarLongAsync(DbConnection connection, string sql, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(token));
    }
}
