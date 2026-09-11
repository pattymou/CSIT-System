using System.Security.Claims;
using SIT.DepartmentSystem.Web.Models.Api;

namespace SIT.DepartmentSystem.Web.Services.Interfaces;

public interface ITestCatalogService
{
    Task<Guid> CreateTestEnvironmentAsync(TestEnvironmentUpsertRequest request, CancellationToken cancellationToken = default);
    Task<bool> UpdateTestEnvironmentAsync(Guid id, TestEnvironmentUpsertRequest request, CancellationToken cancellationToken = default);
    Task<TestEnvironmentDto?> GetTestEnvironmentAsync(Guid id, CancellationToken cancellationToken = default);
    Task<List<TestEnvironmentDto>> ListTestEnvironmentsAsync(CancellationToken cancellationToken = default);

    Task<Guid> CreateEquipmentGroupAsync(EquipmentGroupUpsertRequest request, ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<bool> UpdateEquipmentGroupAsync(Guid id, EquipmentGroupUpsertRequest request, ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<EquipmentGroupDto?> GetEquipmentGroupAsync(Guid id, ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<List<EquipmentGroupDto>> ListEquipmentGroupsAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<EquipmentGroupManagementOptionsDto> GetEquipmentGroupManagementOptionsAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<List<EquipmentGroupEquipmentOptionDto>> ListMatchingEquipmentAsync(string resourceType, string? capabilityTag, ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<Guid> AddEquipmentGroupRequirementAsync(Guid equipmentGroupId, EquipmentGroupRequirementUpsertRequest request, ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<bool> UpdateEquipmentGroupRequirementAsync(Guid id, EquipmentGroupRequirementUpsertRequest request, ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<bool> DeleteEquipmentGroupRequirementAsync(Guid id, ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<List<EquipmentGroupRequirementDto>> ListEquipmentGroupRequirementsAsync(Guid equipmentGroupId, ClaimsPrincipal user, CancellationToken cancellationToken = default);

    Task<Guid> CreateTestCapabilityAsync(TestCapabilityUpsertRequest request, CancellationToken cancellationToken = default);
    Task<bool> UpdateTestCapabilityAsync(Guid id, TestCapabilityUpsertRequest request, CancellationToken cancellationToken = default);
    Task<TestCapabilityDto?> GetTestCapabilityAsync(Guid id, CancellationToken cancellationToken = default);
    Task<List<TestCapabilityDto>> ListTestCapabilitiesAsync(CancellationToken cancellationToken = default);

    Task<Guid> CreateTestPlanTemplateAsync(TestPlanTemplateUpsertRequest request, CancellationToken cancellationToken = default);
    Task<bool> UpdateTestPlanTemplateAsync(Guid id, TestPlanTemplateUpsertRequest request, CancellationToken cancellationToken = default);
    Task<TestPlanTemplateDto?> GetTestPlanTemplateAsync(Guid id, CancellationToken cancellationToken = default);
    Task<List<TestPlanTemplateDto>> ListTestPlanTemplatesAsync(CancellationToken cancellationToken = default);

    Task<Guid> CreateReportTemplateAsync(ReportTemplateUpsertRequest request, CancellationToken cancellationToken = default);
    Task<bool> UpdateReportTemplateAsync(Guid id, ReportTemplateUpsertRequest request, CancellationToken cancellationToken = default);
    Task<ReportTemplateDto?> GetReportTemplateAsync(Guid id, CancellationToken cancellationToken = default);
    Task<List<ReportTemplateDto>> ListReportTemplatesAsync(CancellationToken cancellationToken = default);

    Task<Guid> CreateTestExecutionProfileAsync(TestExecutionProfileUpsertRequest request, CancellationToken cancellationToken = default);
    Task<bool> UpdateTestExecutionProfileAsync(Guid id, TestExecutionProfileUpsertRequest request, CancellationToken cancellationToken = default);
    Task<TestExecutionProfileDto?> GetTestExecutionProfileAsync(Guid id, CancellationToken cancellationToken = default);
    Task<List<TestExecutionProfileDto>> ListTestExecutionProfilesAsync(Guid? testCapabilityId = null, CancellationToken cancellationToken = default);
}
