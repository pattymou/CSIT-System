using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Route("api/test-catalog")]
[Authorize(Policy = SystemAuthorization.Policies.CsitStaff)]
public sealed class TestCatalogController(
    ITestCatalogService service,
    IEnvironmentGroupDeviceService groupDeviceService) : ControllerBase
{
    [HttpGet("environments")]
    public async Task<IActionResult> ListEnvironments(CancellationToken cancellationToken) =>
        Ok(await service.ListTestEnvironmentsAsync(cancellationToken));

    [HttpGet("environments/{id:guid}")]
    public async Task<IActionResult> GetEnvironment(Guid id, CancellationToken cancellationToken) =>
        (await service.GetTestEnvironmentAsync(id, cancellationToken)) is { } item ? Ok(item) : NotFound();

    [HttpPost("environments")]
    public Task<IActionResult> CreateEnvironment(TestEnvironmentUpsertRequest request, CancellationToken cancellationToken) =>
        RunCreateAsync(() => service.CreateTestEnvironmentAsync(request, cancellationToken), nameof(GetEnvironment));

    [HttpPut("environments/{id:guid}")]
    public Task<IActionResult> UpdateEnvironment(Guid id, TestEnvironmentUpsertRequest request, CancellationToken cancellationToken) =>
        RunUpdateAsync(() => service.UpdateTestEnvironmentAsync(id, request, cancellationToken));

    [HttpGet("equipment-groups")]
    public Task<IActionResult> ListEquipmentGroups(CancellationToken cancellationToken) =>
        RunReadAsync(async () => Ok(await service.ListEquipmentGroupsAsync(User, cancellationToken)));

    [HttpGet("equipment-groups/{id:guid}")]
    public Task<IActionResult> GetEquipmentGroup(Guid id, CancellationToken cancellationToken) =>
        RunReadAsync(async () =>
            (await service.GetEquipmentGroupAsync(id, User, cancellationToken)) is { } item ? Ok(item) : NotFound());

    [HttpGet("equipment-groups/management-options")]
    public Task<IActionResult> GetEquipmentGroupManagementOptions(CancellationToken cancellationToken) =>
        RunReadAsync(async () => Ok(await service.GetEquipmentGroupManagementOptionsAsync(User, cancellationToken)));

    [HttpGet("equipment-group-requirements/equipment-options")]
    public Task<IActionResult> ListMatchingEquipment(
        [FromQuery] string resourceType,
        [FromQuery] string? capabilityTag,
        CancellationToken cancellationToken) =>
        RunReadAsync(async () => Ok(await service.ListMatchingEquipmentAsync(resourceType, capabilityTag, User, cancellationToken)));

    [HttpPost("equipment-groups")]
    public Task<IActionResult> CreateEquipmentGroup(EquipmentGroupUpsertRequest request, CancellationToken cancellationToken) =>
        RunCreateAsync(() => service.CreateEquipmentGroupAsync(request, User, cancellationToken), nameof(GetEquipmentGroup));

    [HttpPut("equipment-groups/{id:guid}")]
    public Task<IActionResult> UpdateEquipmentGroup(Guid id, EquipmentGroupUpsertRequest request, CancellationToken cancellationToken) =>
        RunUpdateAsync(() => service.UpdateEquipmentGroupAsync(id, request, User, cancellationToken));

    [HttpGet("equipment-groups/{groupId:guid}/devices")]
    public Task<IActionResult> ListGroupDevices(Guid groupId, CancellationToken cancellationToken) =>
        RunReadAsync(async () => Ok(await groupDeviceService.ListDevicesAsync(groupId, User, cancellationToken)));

    [HttpGet("equipment-groups/{groupId:guid}/device-candidates")]
    public Task<IActionResult> ListGroupDeviceCandidates(
        Guid groupId,
        [FromQuery] string? keyword,
        CancellationToken cancellationToken) =>
        RunReadAsync(async () => Ok(await groupDeviceService.ListCandidatesAsync(groupId, keyword, User, cancellationToken)));

    [HttpPost("equipment-groups/{groupId:guid}/devices")]
    public Task<IActionResult> AddGroupDevices(
        Guid groupId,
        AddEquipmentGroupDevicesRequest request,
        CancellationToken cancellationToken) =>
        RunReadAsync(async () => Ok(await groupDeviceService.AddDevicesAsync(groupId, request, User, cancellationToken)));

    [HttpDelete("equipment-group-devices/{id:guid}")]
    public Task<IActionResult> DeleteGroupDevice(Guid id, CancellationToken cancellationToken) =>
        RunDeleteAsync(() => groupDeviceService.DeleteDeviceAsync(id, User, cancellationToken));

    [HttpPut("equipment-group-devices/{id:guid}/presence")]
    public Task<IActionResult> UpdateGroupDevicePresence(
        Guid id,
        UpdateEquipmentGroupDevicePresenceRequest request,
        CancellationToken cancellationToken) =>
        RunReadAsync(async () => Ok(await groupDeviceService.UpdatePresenceAsync(id, request, User, cancellationToken)));

    [HttpGet("equipment-groups/{groupId:guid}/requirements")]
    public async Task<IActionResult> ListRequirements(Guid groupId, CancellationToken cancellationToken)
        => await RunReadAsync(async () => Ok(await service.ListEquipmentGroupRequirementsAsync(groupId, User, cancellationToken)));

    [HttpPost("equipment-groups/{groupId:guid}/requirements")]
    public Task<IActionResult> CreateRequirement(
        Guid groupId,
        EquipmentGroupRequirementUpsertRequest request,
        CancellationToken cancellationToken) =>
        RunCreateAsync(() => service.AddEquipmentGroupRequirementAsync(groupId, request, User, cancellationToken));

    [HttpPut("equipment-group-requirements/{id:guid}")]
    public Task<IActionResult> UpdateRequirement(
        Guid id,
        EquipmentGroupRequirementUpsertRequest request,
        CancellationToken cancellationToken) =>
        RunUpdateAsync(() => service.UpdateEquipmentGroupRequirementAsync(id, request, User, cancellationToken));

    [HttpDelete("equipment-group-requirements/{id:guid}")]
    public Task<IActionResult> DeleteRequirement(Guid id, CancellationToken cancellationToken) =>
        RunDeleteAsync(() => service.DeleteEquipmentGroupRequirementAsync(id, User, cancellationToken));

    [HttpGet("capabilities")]
    public async Task<IActionResult> ListCapabilities(CancellationToken cancellationToken) =>
        Ok(await service.ListTestCapabilitiesAsync(cancellationToken));

    [HttpGet("capabilities/{id:guid}")]
    public async Task<IActionResult> GetCapability(Guid id, CancellationToken cancellationToken) =>
        (await service.GetTestCapabilityAsync(id, cancellationToken)) is { } item ? Ok(item) : NotFound();

    [HttpPost("capabilities")]
    public Task<IActionResult> CreateCapability(TestCapabilityUpsertRequest request, CancellationToken cancellationToken) =>
        RunCreateAsync(() => service.CreateTestCapabilityAsync(request, cancellationToken), nameof(GetCapability));

    [HttpPut("capabilities/{id:guid}")]
    public Task<IActionResult> UpdateCapability(Guid id, TestCapabilityUpsertRequest request, CancellationToken cancellationToken) =>
        RunUpdateAsync(() => service.UpdateTestCapabilityAsync(id, request, cancellationToken));

    [HttpGet("test-plan-templates")]
    public async Task<IActionResult> ListTestPlanTemplates(CancellationToken cancellationToken) =>
        Ok(await service.ListTestPlanTemplatesAsync(cancellationToken));

    [HttpGet("test-plan-templates/{id:guid}")]
    public async Task<IActionResult> GetTestPlanTemplate(Guid id, CancellationToken cancellationToken) =>
        (await service.GetTestPlanTemplateAsync(id, cancellationToken)) is { } item ? Ok(item) : NotFound();

    [HttpPost("test-plan-templates")]
    public Task<IActionResult> CreateTestPlanTemplate(TestPlanTemplateUpsertRequest request, CancellationToken cancellationToken) =>
        RunCreateAsync(() => service.CreateTestPlanTemplateAsync(request, cancellationToken), nameof(GetTestPlanTemplate));

    [HttpPut("test-plan-templates/{id:guid}")]
    public Task<IActionResult> UpdateTestPlanTemplate(Guid id, TestPlanTemplateUpsertRequest request, CancellationToken cancellationToken) =>
        RunUpdateAsync(() => service.UpdateTestPlanTemplateAsync(id, request, cancellationToken));

    [HttpGet("report-templates")]
    public async Task<IActionResult> ListReportTemplates(CancellationToken cancellationToken) =>
        Ok(await service.ListReportTemplatesAsync(cancellationToken));

    [HttpGet("report-templates/{id:guid}")]
    public async Task<IActionResult> GetReportTemplate(Guid id, CancellationToken cancellationToken) =>
        (await service.GetReportTemplateAsync(id, cancellationToken)) is { } item ? Ok(item) : NotFound();

    [HttpPost("report-templates")]
    public Task<IActionResult> CreateReportTemplate(ReportTemplateUpsertRequest request, CancellationToken cancellationToken) =>
        RunCreateAsync(() => service.CreateReportTemplateAsync(request, cancellationToken), nameof(GetReportTemplate));

    [HttpPut("report-templates/{id:guid}")]
    public Task<IActionResult> UpdateReportTemplate(Guid id, ReportTemplateUpsertRequest request, CancellationToken cancellationToken) =>
        RunUpdateAsync(() => service.UpdateReportTemplateAsync(id, request, cancellationToken));

    [HttpGet("profiles")]
    public async Task<IActionResult> ListProfiles([FromQuery] Guid? testCapabilityId, CancellationToken cancellationToken) =>
        Ok(await service.ListTestExecutionProfilesAsync(testCapabilityId, cancellationToken));

    [HttpGet("profiles/{id:guid}")]
    public async Task<IActionResult> GetProfile(Guid id, CancellationToken cancellationToken) =>
        (await service.GetTestExecutionProfileAsync(id, cancellationToken)) is { } item ? Ok(item) : NotFound();

    [HttpPost("profiles")]
    public Task<IActionResult> CreateProfile(TestExecutionProfileUpsertRequest request, CancellationToken cancellationToken) =>
        RunCreateAsync(() => service.CreateTestExecutionProfileAsync(request, cancellationToken), nameof(GetProfile));

    [HttpPut("profiles/{id:guid}")]
    public Task<IActionResult> UpdateProfile(Guid id, TestExecutionProfileUpsertRequest request, CancellationToken cancellationToken) =>
        RunUpdateAsync(() => service.UpdateTestExecutionProfileAsync(id, request, cancellationToken));

    private async Task<IActionResult> RunCreateAsync(Func<Task<Guid>> action, string? getAction = null)
    {
        try
        {
            var id = await action();
            return getAction is null
                ? StatusCode(StatusCodes.Status201Created, new { id })
                : CreatedAtAction(getAction, new { id }, new { id });
        }
        catch (Exception ex) when (IsExpected(ex)) { return ToError(ex); }
    }

    private async Task<IActionResult> RunReadAsync(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (Exception ex) when (IsExpected(ex)) { return ToError(ex); }
    }

    private async Task<IActionResult> RunUpdateAsync(Func<Task<bool>> action)
    {
        try { return await action() ? NoContent() : NotFound(); }
        catch (Exception ex) when (IsExpected(ex)) { return ToError(ex); }
    }

    private async Task<IActionResult> RunDeleteAsync(Func<Task<bool>> action)
    {
        try { return await action() ? NoContent() : NotFound(); }
        catch (Exception ex) when (IsExpected(ex)) { return ToError(ex); }
    }

    private IActionResult ToError(Exception ex) => ex switch
    {
        UnauthorizedAccessException => Forbid(),
        KeyNotFoundException => NotFound(new { error = ex.Message }),
        InvalidOperationException => Conflict(new { error = ex.Message }),
        ArgumentException => BadRequest(new { error = ex.Message }),
        _ => StatusCode(StatusCodes.Status500InternalServerError)
    };

    private static bool IsExpected(Exception ex) =>
        ex is UnauthorizedAccessException or KeyNotFoundException or InvalidOperationException or ArgumentException;
}
