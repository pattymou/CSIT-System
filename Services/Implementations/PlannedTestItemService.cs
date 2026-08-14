using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Services.Implementations;

public sealed class PlannedTestItemService : IPlannedTestItemService
{
    private readonly AppDbContext _db;

    public PlannedTestItemService(AppDbContext db) => _db = db;

    public async Task<Guid> CreateAsync(PlannedTestItemCreateRequest request, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(request.PlanningSource))
            throw new ArgumentOutOfRangeException(nameof(request.PlanningSource));

        if (!await _db.ModuleRecords.AnyAsync(x => x.Id == request.ModuleRecordId, cancellationToken))
            throw new InvalidOperationException("ModuleRecord does not exist.");

        var profile = await _db.TestExecutionProfiles.AsNoTracking()
            .Include(x => x.TestCapability)
            .Include(x => x.TestEnvironment)
            .Include(x => x.EquipmentGroup)
            .Include(x => x.TestPlanTemplate)
            .Include(x => x.ReportTemplate)
            .SingleOrDefaultAsync(x => x.Id == request.TestExecutionProfileId, cancellationToken)
            ?? throw new InvalidOperationException("Test execution profile does not exist.");

        ValidateActiveProfile(profile);
        if (string.IsNullOrWhiteSpace(profile.TestPlanTemplate.Version) || string.IsNullOrWhiteSpace(profile.ReportTemplate.Version))
            throw new InvalidOperationException("Template version is required.");

        var now = DateTime.UtcNow;
        var entity = new PlannedTestItem
        {
            Id = Guid.NewGuid(),
            ModuleRecordId = request.ModuleRecordId,
            TestCapabilityId = profile.TestCapabilityId,
            TestExecutionProfileId = profile.Id,
            TestEnvironmentId = profile.TestEnvironmentId,
            EquipmentGroupId = profile.EquipmentGroupId,
            TestPlanTemplateId = profile.TestPlanTemplateId,
            TestPlanTemplateVersion = profile.TestPlanTemplate.Version,
            ReportTemplateId = profile.ReportTemplateId,
            ReportTemplateVersion = profile.ReportTemplate.Version,
            EstimatedDurationMinutes = profile.EstimatedDurationMinutes,
            PlanningSource = request.PlanningSource,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.PlannedTestItems.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return entity.Id;
    }

    public async Task<PlannedTestItemDto?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        (await _db.PlannedTestItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)) is { } entity ? Map(entity) : null;

    public async Task<List<PlannedTestItemDto>> ListByModuleRecordAsync(Guid moduleRecordId, CancellationToken cancellationToken = default) =>
        (await _db.PlannedTestItems.AsNoTracking().Where(x => x.ModuleRecordId == moduleRecordId)
            .OrderBy(x => x.CreatedAt).ToListAsync(cancellationToken)).Select(Map).ToList();

    public async Task<bool> ChangeStatusAsync(Guid id, PlannedTestItemStatus status, CancellationToken cancellationToken = default)
    {
        var entity = await _db.PlannedTestItems.FindAsync([id], cancellationToken);
        if (entity is null) return false;
        entity.ChangeStatus(status, DateTime.UtcNow);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static void ValidateActiveProfile(TestExecutionProfile profile)
    {
        if (profile.Status != TestExecutionProfileStatus.Active) throw new InvalidOperationException("Test execution profile is not active.");
        if (profile.EstimatedDurationMinutes <= 0) throw new InvalidOperationException("Profile estimated duration must be greater than zero.");
        if (profile.TestCapability.Status != TestCapabilityStatus.Active) throw new InvalidOperationException("Profile capability is not active.");
        if (profile.TestEnvironment.Status != TestEnvironmentStatus.Active) throw new InvalidOperationException("Profile environment is not active.");
        if (profile.EquipmentGroup.Status != EquipmentGroupStatus.Active) throw new InvalidOperationException("Profile equipment group is not active.");
        if (profile.TestPlanTemplate.Status != TemplateStatus.Published) throw new InvalidOperationException("Profile test plan template is not published.");
        if (profile.ReportTemplate.Status != TemplateStatus.Published) throw new InvalidOperationException("Profile report template is not published.");
    }

    private static PlannedTestItemDto Map(PlannedTestItem x) => new()
    {
        Id = x.Id,
        ModuleRecordId = x.ModuleRecordId,
        TestCapabilityId = x.TestCapabilityId,
        TestExecutionProfileId = x.TestExecutionProfileId,
        TestEnvironmentId = x.TestEnvironmentId,
        EquipmentGroupId = x.EquipmentGroupId,
        TestPlanTemplateId = x.TestPlanTemplateId,
        TestPlanTemplateVersion = x.TestPlanTemplateVersion,
        ReportTemplateId = x.ReportTemplateId,
        ReportTemplateVersion = x.ReportTemplateVersion,
        EstimatedDurationMinutes = x.EstimatedDurationMinutes,
        PlanningSource = x.PlanningSource,
        Status = x.Status,
        CreatedAt = x.CreatedAt,
        UpdatedAt = x.UpdatedAt
    };
}
