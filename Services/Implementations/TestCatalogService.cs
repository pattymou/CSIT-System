using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Services.Implementations;

public sealed class TestCatalogService : ITestCatalogService
{
    private readonly AppDbContext _db;

    public TestCatalogService(AppDbContext db) => _db = db;

    public async Task<Guid> CreateTestEnvironmentAsync(TestEnvironmentUpsertRequest request, CancellationToken cancellationToken = default)
    {
        ValidateEnvironment(request);
        var code = NormalizeCode(request.Code);
        await EnsureUniqueCodeAsync(_db.TestEnvironments, code, null, cancellationToken);
        var now = DateTime.UtcNow;
        var entity = new TestEnvironment { Id = Guid.NewGuid(), CreatedAt = now, UpdatedAt = now };
        Apply(entity, request, code);
        _db.TestEnvironments.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return entity.Id;
    }

    public async Task<bool> UpdateTestEnvironmentAsync(Guid id, TestEnvironmentUpsertRequest request, CancellationToken cancellationToken = default)
    {
        ValidateEnvironment(request);
        var entity = await _db.TestEnvironments.FindAsync([id], cancellationToken);
        if (entity is null) return false;
        if (entity.Status != request.Status && request.Status is TestEnvironmentStatus.Maintenance or TestEnvironmentStatus.Disabled)
        {
            var hasActiveProfile = await _db.TestExecutionProfiles.AnyAsync(
                x => x.TestEnvironmentId == id && x.Status == TestExecutionProfileStatus.Active,
                cancellationToken);
            TestCatalogRules.EnsureResourceCanBeDeactivated(hasActiveProfile, "Test environment");
        }
        var code = NormalizeCode(request.Code);
        await EnsureUniqueCodeAsync(_db.TestEnvironments, code, id, cancellationToken);
        Apply(entity, request, code);
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<TestEnvironmentDto?> GetTestEnvironmentAsync(Guid id, CancellationToken cancellationToken = default) =>
        (await _db.TestEnvironments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)) is { } entity
            ? Map(entity) : null;

    public async Task<List<TestEnvironmentDto>> ListTestEnvironmentsAsync(CancellationToken cancellationToken = default) =>
        (await _db.TestEnvironments.AsNoTracking().OrderBy(x => x.Code).ToListAsync(cancellationToken)).Select(Map).ToList();

    public async Task<Guid> CreateEquipmentGroupAsync(EquipmentGroupUpsertRequest request, CancellationToken cancellationToken = default)
    {
        ValidateGroup(request);
        var code = NormalizeCode(request.Code);
        await EnsureUniqueCodeAsync(_db.EquipmentGroups, code, null, cancellationToken);
        var now = DateTime.UtcNow;
        var entity = new EquipmentGroup { Id = Guid.NewGuid(), CreatedAt = now, UpdatedAt = now };
        Apply(entity, request, code);
        _db.EquipmentGroups.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return entity.Id;
    }

    public async Task<bool> UpdateEquipmentGroupAsync(Guid id, EquipmentGroupUpsertRequest request, CancellationToken cancellationToken = default)
    {
        ValidateGroup(request);
        var entity = await _db.EquipmentGroups.FindAsync([id], cancellationToken);
        if (entity is null) return false;
        if (entity.Status != request.Status && request.Status == EquipmentGroupStatus.Disabled)
        {
            var hasActiveProfile = await _db.TestExecutionProfiles.AnyAsync(
                x => x.EquipmentGroupId == id && x.Status == TestExecutionProfileStatus.Active,
                cancellationToken);
            TestCatalogRules.EnsureResourceCanBeDeactivated(hasActiveProfile, "Equipment group");
        }
        var code = NormalizeCode(request.Code);
        await EnsureUniqueCodeAsync(_db.EquipmentGroups, code, id, cancellationToken);
        Apply(entity, request, code);
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<EquipmentGroupDto?> GetEquipmentGroupAsync(Guid id, CancellationToken cancellationToken = default) =>
        (await _db.EquipmentGroups.AsNoTracking().Include(x => x.Requirements)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken)) is { } entity ? Map(entity) : null;

    public async Task<List<EquipmentGroupDto>> ListEquipmentGroupsAsync(CancellationToken cancellationToken = default) =>
        (await _db.EquipmentGroups.AsNoTracking().Include(x => x.Requirements).OrderBy(x => x.Code)
            .ToListAsync(cancellationToken)).Select(Map).ToList();

    public async Task<Guid> AddEquipmentGroupRequirementAsync(Guid equipmentGroupId, EquipmentGroupRequirementUpsertRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequirement(request);
        if (!await _db.EquipmentGroups.AnyAsync(x => x.Id == equipmentGroupId, cancellationToken))
            throw new KeyNotFoundException($"Equipment group {equipmentGroupId} was not found.");
        await EnsurePreferredEquipmentExistsAsync(request.PreferredEquipmentId, cancellationToken);
        var entity = new EquipmentGroupRequirement { Id = Guid.NewGuid(), EquipmentGroupId = equipmentGroupId };
        Apply(entity, request);
        _db.EquipmentGroupRequirements.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return entity.Id;
    }

    public async Task<bool> UpdateEquipmentGroupRequirementAsync(Guid id, EquipmentGroupRequirementUpsertRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequirement(request);
        var entity = await _db.EquipmentGroupRequirements.FindAsync([id], cancellationToken);
        if (entity is null) return false;
        await EnsurePreferredEquipmentExistsAsync(request.PreferredEquipmentId, cancellationToken);
        Apply(entity, request);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteEquipmentGroupRequirementAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _db.EquipmentGroupRequirements.FindAsync([id], cancellationToken);
        if (entity is null) return false;
        _db.EquipmentGroupRequirements.Remove(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<List<EquipmentGroupRequirementDto>> ListEquipmentGroupRequirementsAsync(Guid equipmentGroupId, CancellationToken cancellationToken = default) =>
        (await _db.EquipmentGroupRequirements.AsNoTracking().Where(x => x.EquipmentGroupId == equipmentGroupId)
            .OrderBy(x => x.ResourceType).ThenBy(x => x.CapabilityTag).ToListAsync(cancellationToken)).Select(Map).ToList();

    public async Task<Guid> CreateTestCapabilityAsync(TestCapabilityUpsertRequest request, CancellationToken cancellationToken = default)
    {
        ValidateCapability(request);
        var code = NormalizeCode(request.Code);
        await EnsureUniqueCodeAsync(_db.TestCapabilities, code, null, cancellationToken);
        var now = DateTime.UtcNow;
        var entity = new TestCapability { Id = Guid.NewGuid(), CreatedAt = now, UpdatedAt = now };
        Apply(entity, request, code);
        _db.TestCapabilities.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return entity.Id;
    }

    public async Task<bool> UpdateTestCapabilityAsync(Guid id, TestCapabilityUpsertRequest request, CancellationToken cancellationToken = default)
    {
        ValidateCapability(request);
        var entity = await _db.TestCapabilities.FindAsync([id], cancellationToken);
        if (entity is null) return false;
        if (entity.Status != request.Status && request.Status == TestCapabilityStatus.Retired)
        {
            var hasActiveProfile = await _db.TestExecutionProfiles.AnyAsync(
                x => x.TestCapabilityId == id && x.Status == TestExecutionProfileStatus.Active,
                cancellationToken);
            TestCatalogRules.EnsureResourceCanBeDeactivated(hasActiveProfile, "Test capability");
        }
        var code = NormalizeCode(request.Code);
        await EnsureUniqueCodeAsync(_db.TestCapabilities, code, id, cancellationToken);
        Apply(entity, request, code);
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<TestCapabilityDto?> GetTestCapabilityAsync(Guid id, CancellationToken cancellationToken = default) =>
        (await _db.TestCapabilities.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)) is { } entity ? Map(entity) : null;

    public async Task<List<TestCapabilityDto>> ListTestCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        (await _db.TestCapabilities.AsNoTracking().OrderBy(x => x.Code).ToListAsync(cancellationToken)).Select(Map).ToList();

    public async Task<Guid> CreateTestPlanTemplateAsync(TestPlanTemplateUpsertRequest request, CancellationToken cancellationToken = default)
    {
        ValidatePlanTemplate(request);
        var code = NormalizeCode(request.Code);
        var version = request.Version.Trim();
        await EnsureUniqueTemplateAsync(_db.TestPlanTemplates, code, version, null, cancellationToken);
        var now = DateTime.UtcNow;
        var entity = new TestPlanTemplate { Id = Guid.NewGuid(), CreatedAt = now, UpdatedAt = now };
        Apply(entity, request, code, version, now);
        _db.TestPlanTemplates.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return entity.Id;
    }

    public async Task<bool> UpdateTestPlanTemplateAsync(Guid id, TestPlanTemplateUpsertRequest request, CancellationToken cancellationToken = default)
    {
        ValidatePlanTemplate(request);
        var entity = await _db.TestPlanTemplates.FindAsync([id], cancellationToken);
        if (entity is null) return false;
        TestCatalogRules.ValidateTemplateUpdate(entity, request);
        if (entity.Status != request.Status && request.Status == TemplateStatus.Retired)
        {
            var hasActiveProfile = await _db.TestExecutionProfiles.AnyAsync(
                x => x.TestPlanTemplateId == id && x.Status == TestExecutionProfileStatus.Active,
                cancellationToken);
            TestCatalogRules.EnsureResourceCanBeDeactivated(hasActiveProfile, "Test plan template");
        }
        var code = NormalizeCode(request.Code);
        var version = request.Version.Trim();
        await EnsureUniqueTemplateAsync(_db.TestPlanTemplates, code, version, id, cancellationToken);
        var now = DateTime.UtcNow;
        Apply(entity, request, code, version, now);
        entity.UpdatedAt = now;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<TestPlanTemplateDto?> GetTestPlanTemplateAsync(Guid id, CancellationToken cancellationToken = default) =>
        (await _db.TestPlanTemplates.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)) is { } entity ? Map(entity) : null;

    public async Task<List<TestPlanTemplateDto>> ListTestPlanTemplatesAsync(CancellationToken cancellationToken = default) =>
        (await _db.TestPlanTemplates.AsNoTracking().OrderBy(x => x.Code).ThenBy(x => x.Version).ToListAsync(cancellationToken)).Select(Map).ToList();

    public async Task<Guid> CreateReportTemplateAsync(ReportTemplateUpsertRequest request, CancellationToken cancellationToken = default)
    {
        ValidateReportTemplate(request);
        var code = NormalizeCode(request.Code);
        var version = request.Version.Trim();
        await EnsureUniqueTemplateAsync(_db.ReportTemplates, code, version, null, cancellationToken);
        var now = DateTime.UtcNow;
        var entity = new ReportTemplate { Id = Guid.NewGuid(), CreatedAt = now, UpdatedAt = now };
        Apply(entity, request, code, version);
        _db.ReportTemplates.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return entity.Id;
    }

    public async Task<bool> UpdateReportTemplateAsync(Guid id, ReportTemplateUpsertRequest request, CancellationToken cancellationToken = default)
    {
        ValidateReportTemplate(request);
        var entity = await _db.ReportTemplates.FindAsync([id], cancellationToken);
        if (entity is null) return false;
        TestCatalogRules.ValidateTemplateUpdate(entity, request);
        if (entity.Status != request.Status && request.Status == TemplateStatus.Retired)
        {
            var hasActiveProfile = await _db.TestExecutionProfiles.AnyAsync(
                x => x.ReportTemplateId == id && x.Status == TestExecutionProfileStatus.Active,
                cancellationToken);
            TestCatalogRules.EnsureResourceCanBeDeactivated(hasActiveProfile, "Report template");
        }
        var code = NormalizeCode(request.Code);
        var version = request.Version.Trim();
        await EnsureUniqueTemplateAsync(_db.ReportTemplates, code, version, id, cancellationToken);
        Apply(entity, request, code, version);
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<ReportTemplateDto?> GetReportTemplateAsync(Guid id, CancellationToken cancellationToken = default) =>
        (await _db.ReportTemplates.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)) is { } entity ? Map(entity) : null;

    public async Task<List<ReportTemplateDto>> ListReportTemplatesAsync(CancellationToken cancellationToken = default) =>
        (await _db.ReportTemplates.AsNoTracking().OrderBy(x => x.Code).ThenBy(x => x.Version).ToListAsync(cancellationToken)).Select(Map).ToList();

    public async Task<Guid> CreateTestExecutionProfileAsync(TestExecutionProfileUpsertRequest request, CancellationToken cancellationToken = default)
    {
        ValidateProfile(request);
        var code = NormalizeCode(request.Code);
        await EnsureUniqueCodeAsync(_db.TestExecutionProfiles, code, null, cancellationToken);
        await ValidateProfileReferencesAsync(request, cancellationToken);
        await EnsureDefaultProfileAvailableAsync(request, null, cancellationToken);
        var now = DateTime.UtcNow;
        var entity = new TestExecutionProfile { Id = Guid.NewGuid(), CreatedAt = now, UpdatedAt = now };
        Apply(entity, request, code);
        _db.TestExecutionProfiles.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return entity.Id;
    }

    public async Task<bool> UpdateTestExecutionProfileAsync(Guid id, TestExecutionProfileUpsertRequest request, CancellationToken cancellationToken = default)
    {
        ValidateProfile(request);
        var entity = await _db.TestExecutionProfiles.FindAsync([id], cancellationToken);
        if (entity is null) return false;
        var code = NormalizeCode(request.Code);
        await EnsureUniqueCodeAsync(_db.TestExecutionProfiles, code, id, cancellationToken);
        await ValidateProfileReferencesAsync(request, cancellationToken);
        await EnsureDefaultProfileAvailableAsync(request, id, cancellationToken);
        Apply(entity, request, code);
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<TestExecutionProfileDto?> GetTestExecutionProfileAsync(Guid id, CancellationToken cancellationToken = default) =>
        (await _db.TestExecutionProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)) is { } entity ? Map(entity) : null;

    public async Task<List<TestExecutionProfileDto>> ListTestExecutionProfilesAsync(Guid? testCapabilityId = null, CancellationToken cancellationToken = default)
    {
        var query = _db.TestExecutionProfiles.AsNoTracking();
        if (testCapabilityId.HasValue) query = query.Where(x => x.TestCapabilityId == testCapabilityId.Value);
        return (await query.OrderBy(x => x.Code).ToListAsync(cancellationToken)).Select(Map).ToList();
    }

    private async Task ValidateProfileReferencesAsync(TestExecutionProfileUpsertRequest request, CancellationToken cancellationToken)
    {
        var capability = await _db.TestCapabilities.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.TestCapabilityId, cancellationToken)
            ?? throw new InvalidOperationException("Test capability does not exist.");
        var environment = await _db.TestEnvironments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.TestEnvironmentId, cancellationToken)
            ?? throw new InvalidOperationException("Test environment does not exist.");
        var group = await _db.EquipmentGroups.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.EquipmentGroupId, cancellationToken)
            ?? throw new InvalidOperationException("Equipment group does not exist.");
        var plan = await _db.TestPlanTemplates.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.TestPlanTemplateId, cancellationToken)
            ?? throw new InvalidOperationException("Test plan template does not exist.");
        var report = await _db.ReportTemplates.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.ReportTemplateId, cancellationToken)
            ?? throw new InvalidOperationException("Report template does not exist.");

        if (request.Status != TestExecutionProfileStatus.Active) return;
        if (capability.Status != TestCapabilityStatus.Active) throw new InvalidOperationException("An active profile requires an active capability.");
        if (environment.Status != TestEnvironmentStatus.Active) throw new InvalidOperationException("An active profile requires an active environment.");
        if (group.Status != EquipmentGroupStatus.Active) throw new InvalidOperationException("An active profile requires an active equipment group.");
        if (plan.Status != TemplateStatus.Published) throw new InvalidOperationException("An active profile requires a published test plan template.");
        if (report.Status != TemplateStatus.Published) throw new InvalidOperationException("An active profile requires a published report template.");
    }

    private async Task EnsureDefaultProfileAvailableAsync(
        TestExecutionProfileUpsertRequest request,
        Guid? excludedId,
        CancellationToken cancellationToken)
    {
        TestCatalogRules.ValidateDefaultProfileShape(request);
        if (request.Status != TestExecutionProfileStatus.Active || !request.IsDefault) return;

        var duplicateExists = await _db.TestExecutionProfiles.AnyAsync(x =>
            x.TestCapabilityId == request.TestCapabilityId &&
            x.Status == TestExecutionProfileStatus.Active &&
            x.IsDefault &&
            (!excludedId.HasValue || x.Id != excludedId.Value), cancellationToken);
        TestCatalogRules.EnsureDefaultProfileAvailable(duplicateExists);
    }

    private async Task EnsurePreferredEquipmentExistsAsync(string? id, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(id) && !await _db.Apparatuses.AnyAsync(x => x.Id == id.Trim(), cancellationToken))
            throw new InvalidOperationException("Preferred equipment does not exist.");
    }

    private static async Task EnsureUniqueCodeAsync<TEntity>(DbSet<TEntity> set, string code, Guid? excludedId, CancellationToken cancellationToken)
        where TEntity : class
    {
        var duplicate = await set.AnyAsync(x =>
            EF.Property<string>(x, "Code") == code &&
            (!excludedId.HasValue || EF.Property<Guid>(x, "Id") != excludedId.Value), cancellationToken);
        if (duplicate) throw new InvalidOperationException($"Code already exists: {code}.");
    }

    private static async Task EnsureUniqueTemplateAsync<TEntity>(DbSet<TEntity> set, string code, string version, Guid? excludedId, CancellationToken cancellationToken)
        where TEntity : class
    {
        var duplicate = await set.AnyAsync(x =>
            EF.Property<string>(x, "Code") == code &&
            EF.Property<string>(x, "Version") == version &&
            (!excludedId.HasValue || EF.Property<Guid>(x, "Id") != excludedId.Value), cancellationToken);
        if (duplicate) throw new InvalidOperationException($"Template already exists: {code} / {version}.");
    }

    private static void ValidateEnvironment(TestEnvironmentUpsertRequest x) { RequireCodeAndName(x.Code, x.Name); Require(x.Category, nameof(x.Category)); Require(x.Site, nameof(x.Site)); EnsureEnum(x.Status); EnsureEnum(x.BookingMode); }
    private static void ValidateGroup(EquipmentGroupUpsertRequest x) { RequireCodeAndName(x.Code, x.Name); EnsureEnum(x.Status); }
    private static void ValidateCapability(TestCapabilityUpsertRequest x) { RequireCodeAndName(x.Code, x.Name); Require(x.Category, nameof(x.Category)); EnsureEnum(x.Status); }
    private static void ValidateRequirement(EquipmentGroupRequirementUpsertRequest x) { Require(x.ResourceType, nameof(x.ResourceType)); if (x.Quantity <= 0) throw new ArgumentOutOfRangeException(nameof(x.Quantity), "Quantity must be greater than zero."); }
    private static void ValidatePlanTemplate(TestPlanTemplateUpsertRequest x) { RequireCodeAndName(x.Code, x.Name); Require(x.Version, nameof(x.Version)); Require(x.CreatedBy, nameof(x.CreatedBy)); EnsureEnum(x.Status); }
    private static void ValidateReportTemplate(ReportTemplateUpsertRequest x) { RequireCodeAndName(x.Code, x.Name); Require(x.Version, nameof(x.Version)); EnsureEnum(x.Status); EnsureEnum(x.TemplateType); }
    private static void ValidateProfile(TestExecutionProfileUpsertRequest x) { RequireCodeAndName(x.Code, x.Name); if (x.EstimatedDurationMinutes <= 0) throw new ArgumentOutOfRangeException(nameof(x.EstimatedDurationMinutes), "Estimated duration must be greater than zero."); EnsureEnum(x.AutomationLevel); EnsureEnum(x.Status); }
    private static void RequireCodeAndName(string code, string name) { Require(code, nameof(code)); Require(name, nameof(name)); }
    private static void Require(string? value, string name) { if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} is required.", name); }
    private static void EnsureEnum<TEnum>(TEnum value) where TEnum : struct, Enum { if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value), value, $"Invalid {typeof(TEnum).Name}."); }
    private static string NormalizeCode(string value) => value.Trim().ToUpperInvariant();
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void Apply(TestEnvironment x, TestEnvironmentUpsertRequest r, string code) { x.Code = code; x.Name = r.Name.Trim(); x.Category = r.Category.Trim(); x.Site = r.Site.Trim(); x.Description = Clean(r.Description); x.Status = r.Status; x.BookingMode = r.BookingMode; }
    private static void Apply(EquipmentGroup x, EquipmentGroupUpsertRequest r, string code) { x.Code = code; x.Name = r.Name.Trim(); x.Description = Clean(r.Description); x.Status = r.Status; }
    private static void Apply(EquipmentGroupRequirement x, EquipmentGroupRequirementUpsertRequest r) { x.ResourceType = r.ResourceType.Trim(); x.CapabilityTag = Clean(r.CapabilityTag); x.Quantity = r.Quantity; x.Required = r.Required; x.AllowAlternative = r.AllowAlternative; x.PreferredEquipmentId = Clean(r.PreferredEquipmentId); }
    private static void Apply(TestCapability x, TestCapabilityUpsertRequest r, string code) { x.Code = code; x.Name = r.Name.Trim(); x.Category = r.Category.Trim(); x.Description = Clean(r.Description); x.Status = r.Status; }
    private static void Apply(TestPlanTemplate x, TestPlanTemplateUpsertRequest r, string code, string version, DateTime now) { x.Code = code; x.Name = r.Name.Trim(); x.Version = version; x.Status = r.Status; x.SourceFilePath = Clean(r.SourceFilePath); x.StructuredDefinition = Clean(r.StructuredDefinition); x.CreatedBy = r.CreatedBy.Trim(); if (r.Status == TemplateStatus.Published && !x.PublishedAt.HasValue) x.PublishedAt = now; }
    private static void Apply(ReportTemplate x, ReportTemplateUpsertRequest r, string code, string version) { x.Code = code; x.Name = r.Name.Trim(); x.Version = version; x.Status = r.Status; x.TemplateType = r.TemplateType; x.TemplateFilePath = Clean(r.TemplateFilePath); x.ResultSchema = Clean(r.ResultSchema); }
    private static void Apply(TestExecutionProfile x, TestExecutionProfileUpsertRequest r, string code) { x.Code = code; x.Name = r.Name.Trim(); x.TestCapabilityId = r.TestCapabilityId; x.TestEnvironmentId = r.TestEnvironmentId; x.EquipmentGroupId = r.EquipmentGroupId; x.TestPlanTemplateId = r.TestPlanTemplateId; x.ReportTemplateId = r.ReportTemplateId; x.EstimatedDurationMinutes = r.EstimatedDurationMinutes; x.AutomationLevel = r.AutomationLevel; x.IsDefault = r.IsDefault; x.Status = r.Status; }

    private static TestEnvironmentDto Map(TestEnvironment x) => new() { Id = x.Id, Code = x.Code, Name = x.Name, Category = x.Category, Site = x.Site, Description = x.Description, Status = x.Status, BookingMode = x.BookingMode, CreatedAt = x.CreatedAt, UpdatedAt = x.UpdatedAt };
    private static EquipmentGroupDto Map(EquipmentGroup x) => new() { Id = x.Id, Code = x.Code, Name = x.Name, Description = x.Description, Status = x.Status, CreatedAt = x.CreatedAt, UpdatedAt = x.UpdatedAt, Requirements = x.Requirements.Select(Map).ToList() };
    private static EquipmentGroupRequirementDto Map(EquipmentGroupRequirement x) => new() { Id = x.Id, EquipmentGroupId = x.EquipmentGroupId, ResourceType = x.ResourceType, CapabilityTag = x.CapabilityTag, Quantity = x.Quantity, Required = x.Required, AllowAlternative = x.AllowAlternative, PreferredEquipmentId = x.PreferredEquipmentId };
    private static TestCapabilityDto Map(TestCapability x) => new() { Id = x.Id, Code = x.Code, Name = x.Name, Category = x.Category, Description = x.Description, Status = x.Status, CreatedAt = x.CreatedAt, UpdatedAt = x.UpdatedAt };
    private static TestPlanTemplateDto Map(TestPlanTemplate x) => new() { Id = x.Id, Code = x.Code, Name = x.Name, Version = x.Version, Status = x.Status, SourceFilePath = x.SourceFilePath, StructuredDefinition = x.StructuredDefinition, CreatedBy = x.CreatedBy, PublishedAt = x.PublishedAt, CreatedAt = x.CreatedAt, UpdatedAt = x.UpdatedAt };
    private static ReportTemplateDto Map(ReportTemplate x) => new() { Id = x.Id, Code = x.Code, Name = x.Name, Version = x.Version, Status = x.Status, TemplateType = x.TemplateType, TemplateFilePath = x.TemplateFilePath, ResultSchema = x.ResultSchema, CreatedAt = x.CreatedAt, UpdatedAt = x.UpdatedAt };
    private static TestExecutionProfileDto Map(TestExecutionProfile x) => new() { Id = x.Id, Code = x.Code, Name = x.Name, TestCapabilityId = x.TestCapabilityId, TestEnvironmentId = x.TestEnvironmentId, EquipmentGroupId = x.EquipmentGroupId, TestPlanTemplateId = x.TestPlanTemplateId, ReportTemplateId = x.ReportTemplateId, EstimatedDurationMinutes = x.EstimatedDurationMinutes, AutomationLevel = x.AutomationLevel, IsDefault = x.IsDefault, Status = x.Status, CreatedAt = x.CreatedAt, UpdatedAt = x.UpdatedAt };
}
