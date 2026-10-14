using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.ValidationRequests;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Route("api/validation-request-configuration")]
[Authorize(Policy = SystemAuthorization.Policies.Administration)]
public sealed class ValidationRequestConfigurationController(IValidationRequestConfigurationService service) : ControllerBase
{
    [HttpGet]
    public Task<ValidationRequestConfigurationDto> Get(CancellationToken ct) => service.GetConfigurationAsync(ct);

    [HttpPost("product-types"), BusinessWrite]
    public async Task<ActionResult<Guid>> AddProduct(SaveValidationProductTypeRequest request, CancellationToken ct) => Ok(await service.SaveProductTypeAsync(null, request, ct));
    [HttpPut("product-types/{id:guid}"), BusinessWrite]
    public async Task<ActionResult<Guid>> UpdateProduct(Guid id, SaveValidationProductTypeRequest request, CancellationToken ct) => Ok(await service.SaveProductTypeAsync(id, request, ct));
    [HttpPut("product-types/{id:guid}/sections"), BusinessWrite]
    public async Task<IActionResult> Sections(Guid id, SetValidationMappingsRequest request, CancellationToken ct) { await service.SetSectionMappingsAsync(id, request.Ids, ct); return NoContent(); }
    [HttpPut("product-types/{id:guid}/test-categories"), BusinessWrite]
    public async Task<IActionResult> Categories(Guid id, SetValidationMappingsRequest request, CancellationToken ct) { await service.SetCategoryMappingsAsync(id, request.Ids, ct); return NoContent(); }

    [HttpPost("fields"), BusinessWrite]
    public async Task<ActionResult<Guid>> AddField(SaveValidationFieldRequest request, CancellationToken ct) => Ok(await service.SaveFieldAsync(null, request, ct));
    [HttpPut("fields/{id:guid}"), BusinessWrite]
    public async Task<ActionResult<Guid>> UpdateField(Guid id, SaveValidationFieldRequest request, CancellationToken ct) => Ok(await service.SaveFieldAsync(id, request, ct));
    [HttpDelete("fields/{id:guid}"), BusinessWrite]
    public async Task<IActionResult> DeleteField(Guid id, CancellationToken ct) { await service.DeleteFieldAsync(id, ct); return NoContent(); }
    [HttpPost("fields/{fieldId:guid}/options"), BusinessWrite]
    public async Task<ActionResult<Guid>> AddOption(Guid fieldId, SaveValidationOptionRequest request, CancellationToken ct) => Ok(await service.SaveOptionAsync(fieldId, null, request, ct));
    [HttpPut("fields/{fieldId:guid}/options/{id:guid}"), BusinessWrite]
    public async Task<ActionResult<Guid>> UpdateOption(Guid fieldId, Guid id, SaveValidationOptionRequest request, CancellationToken ct) => Ok(await service.SaveOptionAsync(fieldId, id, request, ct));
    [HttpDelete("options/{id:guid}"), BusinessWrite]
    public async Task<IActionResult> DeleteOption(Guid id, CancellationToken ct) { await service.DeleteOptionAsync(id, ct); return NoContent(); }
    [HttpPost("fields/{fieldId:guid}/conditions"), BusinessWrite]
    public async Task<ActionResult<Guid>> AddCondition(Guid fieldId, SaveValidationConditionRequest request, CancellationToken ct) => Ok(await service.SaveConditionAsync(fieldId, null, request, ct));
    [HttpPut("fields/{fieldId:guid}/conditions/{id:guid}"), BusinessWrite]
    public async Task<ActionResult<Guid>> UpdateCondition(Guid fieldId, Guid id, SaveValidationConditionRequest request, CancellationToken ct) => Ok(await service.SaveConditionAsync(fieldId, id, request, ct));
    [HttpDelete("conditions/{id:guid}"), BusinessWrite]
    public async Task<IActionResult> DeleteCondition(Guid id, CancellationToken ct) { await service.DeleteConditionAsync(id, ct); return NoContent(); }

    [HttpPost("test-items"), BusinessWrite]
    public async Task<ActionResult<Guid>> AddItem(SaveValidationTestItemRequest request, CancellationToken ct) => Ok(await service.SaveTestItemAsync(null, request, ct));
    [HttpPut("test-items/{id:guid}"), BusinessWrite]
    public async Task<ActionResult<Guid>> UpdateItem(Guid id, SaveValidationTestItemRequest request, CancellationToken ct) => Ok(await service.SaveTestItemAsync(id, request, ct));
    [HttpDelete("test-items/{id:guid}"), BusinessWrite]
    public async Task<IActionResult> DeleteItem(Guid id, CancellationToken ct) { await service.DeleteTestItemAsync(id, ct); return NoContent(); }
    [HttpPost("test-items/{itemId:guid}/parameters"), BusinessWrite]
    public async Task<ActionResult<Guid>> AddParameter(Guid itemId, SaveValidationParameterRequest request, CancellationToken ct) => Ok(await service.SaveParameterAsync(itemId, null, request, ct));
    [HttpPut("test-items/{itemId:guid}/parameters/{id:guid}"), BusinessWrite]
    public async Task<ActionResult<Guid>> UpdateParameter(Guid itemId, Guid id, SaveValidationParameterRequest request, CancellationToken ct) => Ok(await service.SaveParameterAsync(itemId, id, request, ct));

    [HttpPost("versions/{id:guid}/publish"), BusinessWrite]
    public async Task<IActionResult> Publish(Guid id, CancellationToken ct) { await service.PublishAsync(id, ct); return NoContent(); }
    [HttpPost("versions/{id:guid}/clone"), BusinessWrite]
    public async Task<ActionResult<Guid>> Clone(Guid id, CancellationToken ct) => Ok(await service.CloneDraftAsync(id, ct));
}

[ApiController]
[Route("api/validation-requests")]
[Authorize(Policy = SystemAuthorization.Policies.CsitStaff)]
public sealed class ValidationRequestPreviewController(IValidationRequestConfigurationService service) : ControllerBase
{
    [HttpGet("preview")]
    public Task<ValidationRequestPreviewDto> Preview([FromQuery] string productTypeCode, CancellationToken ct) => service.GetPreviewAsync(productTypeCode, ct);
}
