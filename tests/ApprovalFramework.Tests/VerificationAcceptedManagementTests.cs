using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Models.Config;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Implementations;
using SIT.DepartmentSystem.Web.Services.Interfaces;
using SIT.DepartmentSystem.Web.Services.ValidationRequests;

namespace ApprovalFramework.Tests;

public sealed class VerificationAcceptedManagementTests
{
    [Fact]
    public async Task AcceptedContentSaveUpdatesRecordAndPreservesApprovalWithRedactedAudit()
    {
        await using var db = NewDb();
        var (application, record, original) = await SeedAsync(db);
        var service = Service(db);
        var edited = Copy(original);
        edited.FieldValues["MODEL_NAME"] = ["MODEL-NEW"];
        edited.FieldValues["WIFI_VERSION"] = ["Wi-Fi 7"];
        edited.FieldValues["SECRET"] = ["new-secret"];
        var approvedAt = application.AcceptedAt;

        await service.UpdateAcceptedAsync(application.Id, User("leader"), new() { ValidationRequest = edited });

        db.ChangeTracker.Clear();
        var saved = await db.VerificationApplications.SingleAsync(x => x.Id == application.Id);
        var savedRecord = await db.ModuleRecords.SingleAsync(x => x.Id == record.Id);
        var reloaded = JsonSerializer.Deserialize<ValidationRequestSubmissionDto>(saved.ValidationFormPayloadJson!, JsonOptions)!;
        Assert.Equal("MODEL-NEW", reloaded.FieldValues["MODEL_NAME"][0]);
        Assert.Equal("Wi-Fi 7", reloaded.FieldValues["WIFI_VERSION"][0]);
        Assert.Equal("MODEL-NEW", savedRecord.CustomerProductName);
        Assert.Equal("MODEL-NEW", savedRecord.Name);
        Assert.Equal(record.ExpectedEndDate, savedRecord.ExpectedEndDate);
        Assert.Equal(VerificationApplicationStatus.Accepted, saved.Status);
        Assert.Equal(approvedAt, saved.AcceptedAt);
        Assert.Equal(record.Id, saved.ModuleRecordId);
        var audit = string.Concat(await db.ApprovalAuditEvents.Where(x => x.SubmissionId == application.Id)
            .OrderBy(x => x.OccurredAt).Select(x => x.Details).ToListAsync());
        Assert.Contains("[REDACTED]", audit);
        Assert.DoesNotContain("new-secret", audit);
        Assert.DoesNotContain("old-secret", audit);
        Assert.Contains("MODEL-OLD", audit);
        Assert.Contains("MODEL-NEW", audit);
    }

    [Fact]
    public async Task HistoricalWorkIsManualIdempotentAndProtected()
    {
        await using var db = NewDb();
        var (application, record, _) = await SeedAsync(db);
        var service = Service(db);
        Assert.False(await service.CanManageAcceptedAsync(application.Id, User("staff")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.EnableSelectedWorkAsync(application.Id, User("staff")));
        Assert.Empty(await db.ModuleRecordTasks.ToListAsync());

        Assert.Equal(1, await service.EnableSelectedWorkAsync(application.Id, User("leader")));
        Assert.Equal(0, await service.EnableSelectedWorkAsync(application.Id, User("leader")));
        var tasks = await db.ModuleRecordTasks.Include(x => x.Case).ToListAsync();
        Assert.Single(tasks);
        Assert.Equal("WIFI", tasks[0].SourceTestItemCode);
        Assert.Equal(record.Id, tasks[0].Case.RecordId);
        Assert.Single(await db.ApprovalAuditEvents.Where(x => x.SubmissionId == application.Id &&
            x.Action == ApprovalAuditActions.VerificationWorkCreated).ToListAsync());
    }

    [Fact]
    public async Task StructuralEditAddsSelectedWorkButCannotRemoveExecutedWork()
    {
        await using var db = NewDb();
        var (application, _, original) = await SeedAsync(db);
        var service = Service(db);
        await service.EnableSelectedWorkAsync(application.Id, User("leader"));
        var added = Copy(original);
        added.SelectedTestItemCodes.Add("GPS");
        await service.UpdateAcceptedAsync(application.Id, User("leader"), new() { ValidationRequest = added });
        Assert.Equal(["GPS", "WIFI"], (await db.ModuleRecordTasks.ToListAsync())
            .Select(x => x.SourceTestItemCode!).OrderBy(x => x).ToArray());

        var wifi = await db.ModuleRecordTasks.SingleAsync(x => x.SourceTestItemCode == "WIFI");
        wifi.AssignEngineer = "engineer";
        await db.SaveChangesAsync();
        var removed = Copy(added);
        removed.SelectedTestItemCodes.Remove("WIFI");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateAcceptedAsync(
            application.Id, User("leader"), new() { ValidationRequest = removed }));
        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.ModuleRecordTasks.CountAsync());
        Assert.Contains("WIFI", (await db.VerificationApplications.SingleAsync(x => x.Id == application.Id)).ValidationFormPayloadJson!);
    }

    [Fact]
    public async Task UnstartedSelectedWorkCanBeRemovedWithoutRecreatingIt()
    {
        await using var db = NewDb();
        var (application, _, original) = await SeedAsync(db);
        var service = Service(db);
        await service.EnableSelectedWorkAsync(application.Id, User("leader"));
        var added = Copy(original);
        added.SelectedTestItemCodes.Add("GPS");
        await service.UpdateAcceptedAsync(application.Id, User("leader"), new() { ValidationRequest = added });
        db.ChangeTracker.Clear();
        var removed = Copy(added);
        removed.SelectedTestItemCodes.Remove("WIFI");
        await service.UpdateAcceptedAsync(application.Id, User("leader"), new() { ValidationRequest = removed });
        db.ChangeTracker.Clear();
        Assert.Equal("GPS", Assert.Single(await db.ModuleRecordTasks.ToListAsync()).SourceTestItemCode);
    }

    [Fact]
    public async Task MissingHistoricalSnapshotCannotCreateInventedWork()
    {
        await using var db = NewDb();
        var (application, _, _) = await SeedAsync(db);
        db.Entry(application).Property(x => x.ValidationFormPayloadJson).CurrentValue = "{}";
        await db.SaveChangesAsync();
        var service = Service(db);
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.EnableSelectedWorkAsync(application.Id, User("leader")));
        Assert.Contains("snapshot", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await db.ModuleRecordTasks.ToListAsync());
    }

    [Fact]
    public async Task AcceptedContentCannotBeChangedByStaffOrWithoutStaffScope()
    {
        await using var db = NewDb();
        var (application, _, original) = await SeedAsync(db);
        var service = Service(db);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.UpdateAcceptedAsync(
            application.Id, User("staff"), new() { ValidationRequest = Copy(original) }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.UpdateAcceptedAsync(
            application.Id, User("leader", scope: false), new() { ValidationRequest = Copy(original) }));
        Assert.Empty(await db.ApprovalAuditEvents.ToListAsync());
    }

    [Fact]
    public void SnapshotGroupsCoverEveryFieldInOriginalOrderAcrossProductSections()
    {
        var fields = new[] { 170, 160, 151, 120, 111, 110, 10 }.Select(order => Field($"F{order}", order)).ToArray();
        var common = Section("COMMON_PRODUCT_INFO", fields);
        var wifi = Section("TYPE_WIFI", [Field("WIFI_A", 5), Field("WIFI_B", 60)]);
        var cellular = Section("TYPE_CELLULAR_UE", [Field("CELLULAR_A", 3)]);
        Assert.Equal(fields.Length, ValidationRequestPresentation.Groups(common).Sum(x => x.Fields.Count));
        Assert.Equal(fields.Select(x => x.Code).Reverse(), ValidationRequestPresentation.Groups(common)
            .SelectMany(x => x.Fields).Select(x => x.Code));
        Assert.Equal(["WIFI_A", "WIFI_B"], ValidationRequestPresentation.Groups(wifi)
            .SelectMany(x => x.Fields).Select(x => x.Code));
        Assert.Equal("CELLULAR_A", Assert.Single(ValidationRequestPresentation.Groups(cellular)
            .SelectMany(x => x.Fields)).Code);
    }

    [Fact]
    public void CasePageExpandsSnapshotSectionsBeforeSelectedWorkAndKeepsTaskEditingSeparate()
    {
        var root = RepositoryRoot();
        var page = File.ReadAllText(Path.Combine(root, "Components", "Pages", "VerificationWorkDetail.razor"));
        var preview = File.ReadAllText(Path.Combine(root, "Components", "Shared", "ValidationRequestPreview.razor"));
        var field = File.ReadAllText(Path.Combine(root, "Components", "Shared", "ValidationRequestFieldReadOnly.razor"));
        Assert.True(page.IndexOf("ExpandSections=\"true\"", StringComparison.Ordinal) <
                    page.IndexOf("_item.SelectedTestItems", StringComparison.Ordinal));
        Assert.Contains("Preview.Sections.OrderBy", preview);
        Assert.Contains("ValidationRequestPresentation.Groups", preview);
        Assert.Contains("ValidationRequestFieldReadOnly", preview);
        Assert.Contains("Field.Children.OrderBy", field);
        Assert.Contains("RepeatableGroupCount", field);
        Assert.Contains("Field.IsSensitive", field);
        Assert.DoesNotContain("VerificationWorkTaskCard", page);
    }

    private static async Task<(VerificationApplication, ModuleRecord, ValidationRequestSubmissionDto)> SeedAsync(AppDbContext db)
    {
        var now = DateTime.UtcNow.AddDays(-1);
        var department = Guid.NewGuid();
        var team = Guid.NewGuid();
        var module = Guid.NewGuid();
        var record = new ModuleRecord
        {
            Id = Guid.NewGuid(), ModuleId = module, RecordNo = "REC-1", Name = "MODEL-OLD",
            CustomerProductName = "MODEL-OLD", Status = "Open", ExpectedEndDate = new DateOnly(2026, 12, 30),
            CreatedAt = now, UpdatedAt = now
        };
        var request = Submission();
        var application = VerificationApplication.CreateDraft(Guid.NewGuid(), "VA-1", department, team,
            "applicant", "Applicant", "a@example.test", "RD", null,
            new VerificationApplicationContent { ProjectName = "MODEL-OLD", ProductModel = "MODEL-OLD", Customer = "Customer" }, now);
        application.SetValidationRequest(request.TemplateVersionId, request.TemplateVersion, request.ProductTypeCode,
            JsonSerializer.Serialize(request, JsonOptions), now);
        application.Submit(new VerificationApplicationRouting(department, "DA40", "DA40", team,
            "TEAM-A", "Team A", "verification", "leader", "Leader"), now);
        application.Accept(record.Id, record.ExpectedEndDate!.Value, "leader", now);
        db.AddRange(
            new ModuleEntity { Id = module, Code = "verification", Name = "verification", IsEnabled = true },
            Option(department, SystemOptionCategories.Department, "DA40"),
            Option(team, SystemOptionCategories.Team, "TEAM-A"),
            new TeamRouting { Id = Guid.NewGuid(), TeamOptionId = team, LeaderAccount = "leader", LeaderDisplayName = "Leader", IsEnabled = true },
            new ValidationProductType { Id = Guid.NewGuid(), Code = "PRODUCT-A", DisplayLabel = "Product A", TeamOptionId = team, IsEnabled = true },
            record, application);
        await db.SaveChangesAsync();
        return (application, record, request);
    }

    private static ValidationRequestSubmissionDto Submission()
    {
        var version = Guid.NewGuid();
        var category = Guid.NewGuid();
        return new()
        {
            TemplateVersionId = version, TemplateVersion = 1, ProductTypeCode = "PRODUCT-A",
            FieldValues = new(StringComparer.OrdinalIgnoreCase)
            {
                ["MODEL_NAME"] = ["MODEL-OLD"], ["CUSTOMER"] = ["Customer"], ["LOCATION"] = ["TAIPEI"],
                ["DEPARTMENT_CODE"] = ["DA40"], ["PRODUCT_TYPE"] = ["PRODUCT-A"],
                ["WIFI_VERSION"] = ["Wi-Fi 6"], ["SECRET"] = ["old-secret"]
            },
            SelectedTestItemCodes = new(StringComparer.OrdinalIgnoreCase) { "WIFI" },
            FormSnapshot = new("VALIDATION", version, 1, "PRODUCT-A", "Product A",
                [Section("COMMON_PRODUCT_INFO", [Field("MODEL_NAME", 10), Field("CUSTOMER", 20),
                    Field("LOCATION", 30), Field("DEPARTMENT_CODE", 40), Field("PRODUCT_TYPE", 50),
                    Field("SECRET", 60, sensitive: true)]),
                 Section("TYPE_WIFI", [Field("WIFI_VERSION", 10)])],
                [new ValidationTestCategoryDto(category, "RADIO", "Radio", true, true, 10,
                    [new ValidationTestItemDto(Guid.NewGuid(), category, "WIFI", "Wi-Fi", null, null, false, true, false, 10, []),
                     new ValidationTestItemDto(Guid.NewGuid(), category, "GPS", "GPS", null, null, false, true, false, 20, [])])])
        };
    }

    private static ValidationRequestSubmissionDto Copy(ValidationRequestSubmissionDto request) =>
        JsonSerializer.Deserialize<ValidationRequestSubmissionDto>(JsonSerializer.Serialize(request, JsonOptions), JsonOptions)!;

    private static ValidationSectionDto Section(string code, IReadOnlyList<ValidationFieldDto> fields) =>
        new(Guid.NewGuid(), code, code, null, true, true, 10, fields);

    private static ValidationFieldDto Field(string code, int order, bool sensitive = false) =>
        new(Guid.NewGuid(), Guid.NewGuid(), null, code, code, null, null,
            sensitive ? "SensitiveText" : "Text", false, true, order, sensitive, null, null, [], [], []);

    private static SystemOption Option(Guid id, string category, string value) => new()
    {
        Id = id, Category = category, Name = value, Value = value, IsEnabled = true,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };

    private static ClaimsPrincipal User(string account, bool scope = true) => new(new ClaimsIdentity(
        scope ? [new Claim("account", account), new Claim(SystemAuthorization.AccessScopeClaim,
            SystemAuthorization.AccessScopes.CsitStaff)] : [new Claim("account", account)], "test"));

    private static VerificationApplicationService Service(AppDbContext db) => new(
        db, null!, new SystemOptionService(db, null!, null!), new AllowWrite(), null!,
        Options.Create(new DocumentStagingOptions()), null!, null!, new VerificationWorkItemProvisioner(db));

    private static AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private sealed class AllowWrite : IBusinessWriteAuthorizationGuard
    {
        public Task DemandAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SIT.DepartmentSystem.Web.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }
}
