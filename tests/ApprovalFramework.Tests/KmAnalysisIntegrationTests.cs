using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SIT.DepartmentSystem.Web.Controllers;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace ApprovalFramework.Tests;

public sealed class KmAnalysisIntegrationTests
{
    private const string ShaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ShaB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task V2VendorCompleted_PersistsFullJson_AndIsIdempotent()
    {
        var factory = new Factory();
        var outbox = await SeedVendorAsync(factory, "same-name.png", ShaA, "KM-VENDOR-1");
        var callback = Completed(outbox, KmAnalysisSourceTypes.VendorFile, ShaA, "Vendor result");
        var service = new KmAnalysisCallbackService(factory);

        await service.ApplyDocumentStatusAsync(callback);
        await service.ApplyDocumentStatusAsync(callback);

        await using var db = await factory.CreateDbContextAsync();
        var result = Assert.Single(await db.KmDocumentAnalysisResults.ToListAsync());
        Assert.Equal(KmAnalysisSourceTypes.VendorFile, result.SourceType);
        Assert.Equal(callback.CallbackId, result.AnalysisResultId);
        Assert.Contains("sourceSpecific", result.ResultJson);
        Assert.Single(await db.KmAnalysisCallbackReceipts.ToListAsync());
        Assert.Equal(KmIntegrationStatus.Completed,
            (await db.KmIntegrationOutbox.SingleAsync()).Status);

        var conflict = await Assert.ThrowsAsync<KmAnalysisCallbackException>(() =>
            service.ApplyDocumentStatusAsync(callback with { SourceSha256 = ShaB }));
        Assert.Equal(409, conflict.StatusCode);
        Assert.Equal("callback_conflict", conflict.Error);
    }

    [Fact]
    public async Task V2ProcessingAndFailed_UpdateStateWithoutCreatingResult()
    {
        var factory = new Factory();
        var outbox = await SeedVendorAsync(factory, "encrypted.xlsx", ShaA, "KM-VENDOR-2");
        var service = new KmAnalysisCallbackService(factory);
        var started = DateTimeOffset.UtcNow;
        await service.ApplyDocumentStatusAsync(new("2.0", Guid.NewGuid(), outbox.EventId,
            outbox.Id, outbox.KmJobId!, "CSIT", KmAnalysisSourceTypes.VendorFile,
            outbox.SourceEntityId, outbox.DocumentId, 1, ShaA, "Processing",
            new(started, null, null), null, null));
        await service.ApplyDocumentStatusAsync(new("2.0", Guid.NewGuid(), outbox.EventId,
            outbox.Id, outbox.KmJobId!, "CSIT", KmAnalysisSourceTypes.VendorFile,
            outbox.SourceEntityId, outbox.DocumentId, 1, ShaA, "Failed",
            new(started, null, DateTimeOffset.UtcNow), null,
            new("ENCRYPTED_DOCUMENT", "Password protected.", false)));

        await using var db = await factory.CreateDbContextAsync();
        Assert.Empty(await db.KmDocumentAnalysisResults.ToListAsync());
        Assert.Equal(2, await db.KmAnalysisCallbackReceipts.CountAsync());
        Assert.Equal(KmIntegrationStatus.Failed, (await db.KmIntegrationOutbox.SingleAsync()).Status);
    }

    [Fact]
    public async Task V2VerificationAndEquipment_RequireFormalApprovedSources()
    {
        var factory = new Factory();
        var verification = await SeedVerificationAsync(factory, accepted: true, "KM-VERIFY-1");
        var equipment = await SeedEquipmentAsync(factory, approved: true, "KM-EQUIP-1");
        var service = new KmAnalysisCallbackService(factory);

        await service.ApplyDocumentStatusAsync(Completed(verification,
            KmAnalysisSourceTypes.VerificationApplicationAttachment, ShaA, "Verification"));
        await service.ApplyDocumentStatusAsync(Completed(equipment,
            KmAnalysisSourceTypes.EquipmentAttachment, ShaB, "Equipment"));

        await using (var db = await factory.CreateDbContextAsync())
            Assert.Equal(2, await db.KmDocumentAnalysisResults.CountAsync());

        var otherFactory = new Factory();
        var unapproved = await SeedVerificationAsync(otherFactory, accepted: false, "KM-VERIFY-2");
        var rejected = await Assert.ThrowsAsync<KmAnalysisCallbackException>(() =>
            new KmAnalysisCallbackService(otherFactory).ApplyDocumentStatusAsync(Completed(unapproved,
                KmAnalysisSourceTypes.VerificationApplicationAttachment, ShaA, "Rejected")));
        Assert.Equal(422, rejected.StatusCode);
        Assert.Equal("source_not_formal", rejected.Error);
    }

    [Fact]
    public async Task V2Callback_RejectsWrongCorrelationShaAndDeletionHold()
    {
        var factory = new Factory();
        var outbox = await SeedEquipmentAsync(factory, approved: true, "KM-CORRELATION");
        var service = new KmAnalysisCallbackService(factory);
        var valid = Completed(outbox, KmAnalysisSourceTypes.EquipmentAttachment, ShaB, "Equipment");

        foreach (var callback in new[]
        {
            valid with { CallbackId = Guid.NewGuid(), SourceEntityId = Guid.NewGuid() },
            valid with { CallbackId = Guid.NewGuid(), CsitJobId = Guid.NewGuid() },
            valid with { CallbackId = Guid.NewGuid(), EventId = Guid.NewGuid() }
        })
        {
            var mismatch = await Assert.ThrowsAsync<KmAnalysisCallbackException>(() =>
                service.ApplyDocumentStatusAsync(callback));
            Assert.Equal(409, mismatch.StatusCode);
            Assert.Equal("callback_mismatch", mismatch.Error);
        }

        var sha = await Assert.ThrowsAsync<KmAnalysisCallbackException>(() =>
            service.ApplyDocumentStatusAsync(valid with { CallbackId = Guid.NewGuid(), SourceSha256 = ShaA }));
        Assert.Equal("source_sha256_mismatch", sha.Error);

        await using (var db = await factory.CreateDbContextAsync())
        {
            var file = await db.ApparatusFiles.SingleAsync();
            db.EquipmentFileDeletionJobs.Add(EquipmentFileDeletionJob.Create(file, "admin", true,
                DateTime.UtcNow, file.Id));
            await db.SaveChangesAsync();
        }
        var held = await Assert.ThrowsAsync<KmAnalysisCallbackException>(() =>
            service.ApplyDocumentStatusAsync(valid with { CallbackId = Guid.NewGuid() }));
        Assert.Equal(422, held.StatusCode);
        Assert.Equal("deletion_in_progress", held.Error);
    }

    [Fact]
    public async Task SameNameAndSameHash_RemainIndependentSourceResults()
    {
        var factory = new Factory();
        var first = await SeedVendorAsync(factory, "same.png", ShaA, "KM-SAME-1");
        var second = await SeedVendorAsync(factory, "same.png", ShaA, "KM-SAME-2");
        var service = new KmAnalysisCallbackService(factory);
        await service.ApplyDocumentStatusAsync(Completed(first, KmAnalysisSourceTypes.VendorFile, ShaA, "One"));
        await service.ApplyDocumentStatusAsync(Completed(second, KmAnalysisSourceTypes.VendorFile, ShaA, "Two"));

        await using var db = await factory.CreateDbContextAsync();
        var rows = await db.KmDocumentAnalysisResults.OrderBy(x => x.SourceEntityId).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.NotEqual(rows[0].SourceEntityId, rows[1].SourceEntityId);
        Assert.Equal(rows[0].SourceSha256, rows[1].SourceSha256);
    }

    [Fact]
    public async Task TestReportAnalysisResult_RequiresCompletedV1Job_AndPreservesV1State()
    {
        var factory = new Factory();
        var outbox = await SeedTestReportAsync(factory, completed: true, "KM-REPORT-1");
        var callback = new KmV2AnalysisResultCallback("2.0", Guid.NewGuid(), Guid.NewGuid(),
            "CSIT", KmAnalysisSourceTypes.TestReport, outbox.SourceEntityId, outbox.DocumentId,
            outbox.Version, outbox.Id, outbox.KmJobId!, ShaA, "2.0", Metadata("Report"),
            DateTimeOffset.UtcNow, null);
        await new KmAnalysisCallbackService(factory).ApplyTestReportResultAsync(callback);

        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.Single(await db.KmDocumentAnalysisResults.ToListAsync());
            Assert.Equal(KmIntegrationStatus.Completed,
                (await db.KmIntegrationOutbox.SingleAsync()).Status);
        }

        var pendingFactory = new Factory();
        var pending = await SeedTestReportAsync(pendingFactory, completed: false, "KM-REPORT-2");
        var pendingCallback = callback with
        {
            AnalysisResultId = Guid.NewGuid(), CallbackId = Guid.NewGuid(),
            SourceEntityId = pending.SourceEntityId, DocumentId = pending.DocumentId,
            CsitJobId = pending.Id, KmJobId = pending.KmJobId!
        };
        var error = await Assert.ThrowsAsync<KmAnalysisCallbackException>(() =>
            new KmAnalysisCallbackService(pendingFactory).ApplyTestReportResultAsync(pendingCallback));
        Assert.Equal("job_not_completed", error.Error);
    }

    [Fact]
    public async Task TestReportAnalysisResult_PreservesVersionHistoryAndSupersession()
    {
        var factory = new Factory();
        var documentId = Guid.NewGuid();
        var first = await SeedTestReportAsync(factory, completed: true, "KM-HISTORY-1", documentId, 1);
        var second = await SeedTestReportAsync(factory, completed: true, "KM-HISTORY-2", documentId, 2);
        var service = new KmAnalysisCallbackService(factory);
        var firstResultId = Guid.NewGuid();
        await service.ApplyTestReportResultAsync(Analysis(first, firstResultId, null, "Version one"));
        await service.ApplyTestReportResultAsync(Analysis(second, Guid.NewGuid(), firstResultId, "Version two"));

        await using var db = await factory.CreateDbContextAsync();
        var history = await db.KmDocumentAnalysisResults.OrderBy(x => x.Version).ToListAsync();
        Assert.Equal(2, history.Count);
        Assert.Equal([1, 2], history.Select(x => x.Version));
        Assert.NotNull(history[0].SupersededAt);
        Assert.Equal(history[0].Id, history[1].SupersedesResultId);
        Assert.All(await db.KmIntegrationOutbox.ToListAsync(),
            x => Assert.Equal(KmIntegrationStatus.Completed, x.Status));
    }

    [Fact]
    public async Task QueryAndReview_EnforceTeamVisibility_AndPreserveRawResult()
    {
        var factory = new Factory();
        var team = Guid.NewGuid();
        var outbox = await SeedVerificationAsync(factory, accepted: true, "KM-ACL", team);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.TeamRoutings.Add(new TeamRouting
            {
                Id = Guid.NewGuid(), TeamOptionId = team, LeaderAccount = "leader-a",
                LeaderDisplayName = "Leader A", IsEnabled = true,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        await new KmAnalysisCallbackService(factory).ApplyDocumentStatusAsync(Completed(outbox,
            KmAnalysisSourceTypes.VerificationApplicationAttachment, ShaA, "ACL title"));
        var service = new KmAnalysisQueryService(factory);
        var visible = await service.ListAsync(User("leader-a", editor: true), null, null, 1, 200);
        var hidden = await service.ListAsync(User("leader-b", editor: true), null, null, 1, 50);
        Assert.Single(visible.Items);
        Assert.Empty(hidden.Items);
        Assert.Equal(100, visible.PageSize);

        var before = (await service.GetAsync(User("leader-a", true), visible.Items[0].Id))!.Result.GetRawText();
        using var corrections = JsonDocument.Parse("{\"document\":{\"title\":\"Human title\"}}");
        var reviewed = await service.ReviewAsync(User("leader-a", true), visible.Items[0].Id,
            new("Verified", corrections.RootElement.Clone(), "Checked"));
        Assert.Equal(KmAnalysisReviewStatuses.Verified, reviewed.ReviewStatus);
        Assert.Single(reviewed.Reviews);
        Assert.Equal(before, reviewed.Result.GetRawText());
        Assert.Contains(typeof(KmAnalysisAdministrationController).GetMethod("Review")!
            .GetCustomAttributes(), x => x.GetType() == typeof(BusinessWriteAttribute));
    }

    [Fact]
    public void MetadataContract_RegistersFiveSources_AndRejectsChunks()
    {
        Assert.Equal(5, KmAnalysisSourceTypes.All.Distinct().Count());
        foreach (var source in KmAnalysisSourceTypes.All)
        {
            Assert.True(KmAnalysisMetadataValidator.TryValidate(Metadata(source), "2.0", out var parsed));
            Assert.Equal(source, parsed!.Title);
        }
        using var unsafeJson = JsonDocument.Parse(Metadata("unsafe").GetRawText()
            .Replace("\"sourceSpecific\":{}", "\"sourceSpecific\":{\"chunks\":[\"secret\"]}"));
        Assert.False(KmAnalysisMetadataValidator.TryValidate(unsafeJson.RootElement, "2.0", out _));
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task MigrationAndCallbackTransaction_RunAgainstIsolatedPostgreSql()
    {
        var connection = Environment.GetEnvironmentVariable("CSIT_KM_ANALYSIS_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) return;
        var info = new NpgsqlConnectionStringBuilder(connection);
        Assert.Equal("127.0.0.1", info.Host);
        Assert.Equal(55436, info.Port);
        Assert.StartsWith("csit_km_analysis_e2e_", info.Database, StringComparison.Ordinal);
        Assert.Equal("postgres", info.Username);
        Assert.True(string.IsNullOrEmpty(info.Password));

        var factory = new PostgreSqlFactory(connection);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var applied = await db.Database.GetAppliedMigrationsAsync();
            Assert.Contains("20261102075735_AddDocumentLibraryFileDeletionLifecycle", applied);
            Assert.DoesNotContain("20261105060913_AddKmDocumentAnalysisResultsV14", applied);
        }

        var tablesBefore = await ReadPublicTablesAsync(connection);
        var columnsBefore = await ReadPublicColumnsAsync(connection, tablesBefore);
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();
            Assert.Contains("20261105060913_AddKmDocumentAnalysisResultsV14",
                await db.Database.GetAppliedMigrationsAsync());
        }

        var tablesAfter = await ReadPublicTablesAsync(connection);
        Assert.Equal(new[]
        {
            "km_analysis_callback_receipts",
            "km_analysis_review_revisions",
            "km_document_analysis_results"
        }, tablesAfter.Except(tablesBefore).Order().ToArray());
        Assert.Equal(columnsBefore, await ReadPublicColumnsAsync(connection, tablesBefore));

        var constraints = await ReadNamesAsync(connection, """
            SELECT conname
            FROM pg_constraint
            WHERE conrelid IN (
                'km_analysis_callback_receipts'::regclass,
                'km_document_analysis_results'::regclass,
                'km_analysis_review_revisions'::regclass)
            ORDER BY conname
            """);
        Assert.Contains("ck_km_analysis_callback_status", constraints);
        Assert.Contains("ck_km_analysis_source_type", constraints);
        Assert.Contains("ck_km_analysis_review_decision", constraints);
        Assert.Contains("FK_km_document_analysis_results_km_integration_outbox_outbox_id", constraints);

        var indexes = await ReadNamesAsync(connection, """
            SELECT indexname
            FROM pg_indexes
            WHERE schemaname='public' AND tablename IN (
                'km_analysis_callback_receipts',
                'km_document_analysis_results',
                'km_analysis_review_revisions')
            ORDER BY indexname
            """);
        Assert.Contains("IX_km_document_analysis_results_analysis_result_id", indexes);
        Assert.Contains("IX_km_document_analysis_results_source_type_source_entity_id_d~", indexes);
        Assert.Contains("IX_km_analysis_review_revisions_analysis_result_id_revision", indexes);

        var outbox = await SeedVendorAsync(factory, "postgres.pdf", ShaA, "KM-PG-1");
        var callback = Completed(outbox, KmAnalysisSourceTypes.VendorFile, ShaA, "PostgreSQL result");
        var callbacks = new KmAnalysisCallbackService(factory);
        await callbacks.ApplyDocumentStatusAsync(callback);
        await callbacks.ApplyDocumentStatusAsync(callback);

        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.Single(await db.KmAnalysisCallbackReceipts.ToListAsync());
            Assert.Single(await db.KmDocumentAnalysisResults.ToListAsync());
            Assert.Equal(KmIntegrationStatus.Completed,
                (await db.KmIntegrationOutbox.SingleAsync()).Status);
        }

        var conflict = await Assert.ThrowsAsync<KmAnalysisCallbackException>(() =>
            callbacks.ApplyDocumentStatusAsync(callback with { SourceSha256 = ShaB }));
        Assert.Equal(409, conflict.StatusCode);
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.Single(await db.KmAnalysisCallbackReceipts.ToListAsync());
            Assert.Single(await db.KmDocumentAnalysisResults.ToListAsync());
        }

        var query = new KmAnalysisQueryService(factory);
        var page = await query.ListAsync(User("reviewer", true), null, null, 1, 50);
        var before = (await query.GetAsync(User("reviewer", true), Assert.Single(page.Items).Id))!
            .Result.GetRawText();
        using (var corrections = JsonDocument.Parse("{\"document\":{\"title\":\"Reviewed\"}}"))
            await query.ReviewAsync(User("reviewer", true), page.Items[0].Id,
                new("Verified", corrections.RootElement.Clone(), "PostgreSQL review"));
        var after = (await query.GetAsync(User("reviewer", true), page.Items[0].Id))!;
        Assert.Equal(before, after.Result.GetRawText());
        Assert.Single(after.Reviews);

        await AssertSqlStateAsync(connection, PostgresErrorCodes.ForeignKeyViolation, """
            INSERT INTO km_analysis_callback_receipts
                (callback_id,outbox_id,endpoint,status,payload_sha256,received_at)
            VALUES (@callback,@outbox,'test','Completed',repeat('c',64),NOW())
            """, Guid.NewGuid(), Guid.NewGuid());
        await AssertSqlStateAsync(connection, PostgresErrorCodes.CheckViolation, """
            INSERT INTO km_analysis_callback_receipts
                (callback_id,outbox_id,endpoint,status,payload_sha256,received_at)
            VALUES (@callback,@outbox,'test','Invalid',repeat('c',64),NOW())
            """, Guid.NewGuid(), outbox.Id);
        await AssertSqlStateAsync(connection, PostgresErrorCodes.UniqueViolation, """
            INSERT INTO km_analysis_callback_receipts
                (callback_id,outbox_id,endpoint,status,payload_sha256,received_at)
            VALUES (@callback,@outbox,'test','Completed',repeat('c',64),NOW())
            """, callback.CallbackId, outbox.Id);
    }

    private static KmV2DocumentJobStatusCallback Completed(KmIntegrationOutbox outbox,
        string sourceType, string sha, string title) => new("2.0", Guid.NewGuid(), outbox.EventId,
        outbox.Id, outbox.KmJobId!, "CSIT", sourceType, outbox.SourceEntityId,
        outbox.DocumentId, outbox.Version, sha, "Completed",
        new(DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow, null),
        Metadata(title), null);

    private static KmV2AnalysisResultCallback Analysis(KmIntegrationOutbox outbox,
        Guid analysisResultId, Guid? supersedes, string title) => new("2.0", analysisResultId,
        Guid.NewGuid(), "CSIT", KmAnalysisSourceTypes.TestReport, outbox.SourceEntityId,
        outbox.DocumentId, outbox.Version, outbox.Id, outbox.KmJobId!, ShaA, "2.0",
        Metadata(title), DateTimeOffset.UtcNow, supersedes);

    private static JsonElement Metadata(string title)
    {
        var json = """
        {"metadataSchemaVersion":"2.0","document":{"title":"__TITLE__","documentType":"Other","summary":null,"language":null,"documentDate":null,"authors":[],"department":null,"documentVersionLabel":null,"keywords":[],"tags":[]},"technical":{"productNames":[],"modelNumbers":[],"vendorNames":[],"technologies":[],"standards":[],"softwareVersions":[],"hardwareVersions":[],"projectNames":[],"testItems":[],"measurements":[],"issueKeys":[],"errorCodes":[]},"extraction":{"sourceFormat":"txt","method":"Text","pageCount":null,"sheetNames":[],"extractedCharacterCount":1,"ocrUsed":false},"indexing":{"status":"Completed","indexId":"idx-1","chunkCount":1,"indexedAt":"2026-11-05T01:08:24Z"},"warnings":[],"sourceSpecific":{}}
        """.Replace("__TITLE__", title.Replace("\"", "\\\""));
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static async Task<KmIntegrationOutbox> SeedVendorAsync(IDbContextFactory<AppDbContext> factory,
        string name, string sha, string jobId)
    {
        var now = DateTime.UtcNow;
        var vendor = new Vendor
        {
            Id = Guid.NewGuid(), Name = "KM Test Vendor", NormalizedName = "KM TEST VENDOR",
            Types = [], StorageFolderName = "km-test-vendor", CreatedBy = "uploader",
            CreatedAt = now, UpdatedBy = "uploader", UpdatedAt = now
        };
        var file = new VendorFile
        {
            Id = Guid.NewGuid(), VendorId = vendor.Id, Vendor = vendor, OriginalFileName = name,
            StoredFileName = Guid.NewGuid() + ".bin", FolderPath = "C:\\formal", ContentType = "application/octet-stream",
            FileSize = 12, Sha256 = sha, ArchiveStatus = FileArchiveStatuses.Archived,
            ArchiveUpdatedAt = now, UploadedBy = "uploader", UploadedAt = now,
            ApprovalOperationId = Guid.NewGuid(), ApprovedAt = now, ApprovedBy = "leader"
        };
        var outbox = Sent(KmIntegrationOutbox.ForArchivedVendorFile(file, now), jobId, now);
        await using var db = await factory.CreateDbContextAsync();
        db.Vendors.Add(vendor); db.VendorFiles.Add(file); db.KmIntegrationOutbox.Add(outbox);
        db.ApprovalAuditEvents.Add(ApprovalAuditEvent.Create(file.ApprovalOperationId!.Value,
            "vendors", "vendor", ApprovalAuditActions.ApprovalBypassed,
            "leader", now, null, null));
        await db.SaveChangesAsync();
        return outbox;
    }

    private static async Task<KmIntegrationOutbox> SeedVerificationAsync(IDbContextFactory<AppDbContext> factory,
        bool accepted, string jobId, Guid? teamId = null)
    {
        var now = DateTime.UtcNow;
        var app = (VerificationApplication)Activator.CreateInstance(typeof(VerificationApplication), true)!;
        Set(app, nameof(VerificationApplication.Id), Guid.NewGuid());
        Set(app, nameof(VerificationApplication.ApplicationNo), "VA-TEST");
        Set(app, nameof(VerificationApplication.Status), accepted
            ? VerificationApplicationStatus.Accepted : VerificationApplicationStatus.Submitted);
        Set(app, nameof(VerificationApplication.ApplicantAccount), "applicant");
        Set(app, nameof(VerificationApplication.AssignedLeaderAccount), "leader-a");
        Set(app, nameof(VerificationApplication.TeamOptionId), teamId ?? Guid.NewGuid());
        Set(app, nameof(VerificationApplication.CreatedAt), now);
        Set(app, nameof(VerificationApplication.UpdatedAt), now);
        var document = new DocumentLibraryFile
        {
            Id = Guid.NewGuid(), FolderId = Guid.NewGuid(), OriginalFileName = "attachment.xlsx",
            StandardFileName = "standard.xlsx", ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            UploadedBy = "applicant", UploadedAt = now, FileSize = 42, Sha256 = ShaA,
            RelativePath = "formal\\standard.xlsx", StorageArea = DocumentStorageAreas.Library
        };
        var file = new VerificationApplicationFile
        {
            Id = Guid.NewGuid(), VerificationApplicationId = app.Id, VerificationApplication = app,
            FileName = "attachment.xlsx", FilePath = "formal", ContentType = document.ContentType,
            FileSize = document.FileSize, Sha256 = ShaA, UploadedBy = "applicant", CreatedAt = now,
            DocumentLibraryFileId = document.Id, DocumentLibraryFile = document
        };
        app.Files.Add(file);
        Set(app, nameof(VerificationApplication.Status), VerificationApplicationStatus.Accepted);
        var outbox = Sent(KmIntegrationOutbox.ForArchivedVerificationAttachment(app, file, now), jobId, now);
        if (!accepted) Set(app, nameof(VerificationApplication.Status), VerificationApplicationStatus.Submitted);
        await using var db = await factory.CreateDbContextAsync();
        db.VerificationApplications.Add(app); db.DocumentLibraryFiles.Add(document);
        db.KmIntegrationOutbox.Add(outbox); await db.SaveChangesAsync();
        return outbox;
    }

    private static async Task<KmIntegrationOutbox> SeedEquipmentAsync(IDbContextFactory<AppDbContext> factory,
        bool approved, string jobId)
    {
        var now = DateTime.UtcNow;
        var equipment = new Apparatus
        {
            Id = "EQ-1", ModuleCode = "equipment", Name = "Equipment", Kind = "Instrument",
            OwnerTeamOptionId = Guid.NewGuid(), CreatedAt = now, UpdatedAt = now
        };
        var file = new ApparatusFile
        {
            Id = Guid.NewGuid(), ApparatusId = equipment.Id, Apparatus = equipment,
            FileName = "manual.pdf", OriginalFileName = "manual.pdf", FilePath = "C:\\formal",
            ContentType = "application/pdf", FileSize = 50, Sha256 = ShaB,
            ArchiveStatus = FileArchiveStatuses.Archived, ArchiveUpdatedAt = now, CreatedAt = now
        };
        equipment.Files.Add(file);
        var outbox = Sent(KmIntegrationOutbox.ForArchivedEquipmentDocument(equipment, file, now), jobId, now);
        await using var db = await factory.CreateDbContextAsync();
        db.Apparatuses.Add(equipment); db.KmIntegrationOutbox.Add(outbox);
        if (approved) db.ApprovalAuditEvents.Add(ApprovalAuditEvent.Create(file.Id,
            "equipment", "equipment", ApprovalAuditActions.ApprovalBypassed, "admin", now, null, null));
        await db.SaveChangesAsync();
        return outbox;
    }

    private static async Task<KmIntegrationOutbox> SeedTestReportAsync(IDbContextFactory<AppDbContext> factory,
        bool completed, string jobId, Guid? documentId = null, int version = 1)
    {
        var now = DateTime.UtcNow;
        var file = new ModuleCaseFile
        {
            Id = Guid.NewGuid(), RecordId = Guid.NewGuid(), FileName = "report.pdf",
            FilePath = "C:\\formal", ContentType = "application/pdf", FileSize = 20,
            CreatedAt = now, UpdatedAt = now, DocumentId = documentId ?? Guid.NewGuid(), VersionNo = version,
            FileKind = ModuleCaseFileKinds.TestReport, ReviewStatus = TestReportReviewStatuses.Approved,
            IsRawDataExported = true, Sha256 = ShaA
        };
        var outbox = Sent(KmIntegrationOutbox.ForCommittedTestReport(file, now), jobId, now);
        if (completed) outbox.CallbackCompleted(now);
        await using var db = await factory.CreateDbContextAsync();
        db.ModuleCaseFiles.Add(file); db.KmIntegrationOutbox.Add(outbox); await db.SaveChangesAsync();
        return outbox;
    }

    private static KmIntegrationOutbox Sent(KmIntegrationOutbox outbox, string jobId, DateTime now)
    {
        var lease = Guid.NewGuid(); outbox.Claim(lease, now, TimeSpan.FromMinutes(1));
        outbox.Sent(lease, jobId, now); return outbox;
    }

    private static ClaimsPrincipal User(string account, bool editor) => new(new ClaimsIdentity(
        new[] { new Claim("account", account), new Claim(SystemAuthorization.AccessScopeClaim,
            SystemAuthorization.AccessScopes.CsitStaff), new Claim(SystemAuthorization.DataAccessLevelClaim,
            editor ? SystemAuthorization.DataAccessLevels.Editor : SystemAuthorization.DataAccessLevels.ReadOnly) }, "test"));

    private static void Set(object target, string property, object? value) =>
        target.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(target, value);

    private static async Task<string[]> ReadPublicTablesAsync(string connection)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = """
            SELECT table_name FROM information_schema.tables
            WHERE table_schema='public' AND table_type='BASE TABLE'
            ORDER BY table_name
            """;
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) values.Add(reader.GetString(0));
        return values.ToArray();
    }

    private static async Task<string[]> ReadPublicColumnsAsync(string connection,
        IReadOnlyCollection<string> tables)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = """
            SELECT table_name || '|' || ordinal_position || '|' || column_name || '|' ||
                   data_type || '|' || is_nullable || '|' || COALESCE(column_default,'')
            FROM information_schema.columns
            WHERE table_schema='public' AND table_name = ANY(@tables)
            ORDER BY table_name, ordinal_position
            """;
        command.Parameters.AddWithValue("tables", tables.ToArray());
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) values.Add(reader.GetString(0));
        return values.ToArray();
    }

    private static async Task<string[]> ReadNamesAsync(string connection, string sql)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = sql;
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) values.Add(reader.GetString(0));
        return values.ToArray();
    }

    private static async Task AssertSqlStateAsync(string connection, string sqlState, string sql,
        Guid callbackId, Guid outboxId)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("callback", callbackId);
        command.Parameters.AddWithValue("outbox", outboxId);
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(sqlState, error.SqlState);
    }

    private sealed class Factory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        public AppDbContext CreateDbContext() => new(_options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class PostgreSqlFactory(string connection) : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connection).Options;
        public AppDbContext CreateDbContext() => new(_options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
