using Microsoft.AspNetCore.Mvc;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Route("api/menu-management")]
public class MenuManagementController : ControllerBase
{
    private readonly IMenuManagementService _service;

    public MenuManagementController(IMenuManagementService service)
    {
        _service = service;
    }

    [HttpGet("sections")]
    public async Task<ActionResult<List<MenuSectionDto>>> GetSections()
    {
        return Ok(await _service.GetSectionsAsync());
    }

    [HttpGet("sections/{sectionId:guid}/items")]
    public async Task<ActionResult<List<MenuItemDto>>> GetItems(Guid sectionId)
    {
        return Ok(await _service.GetItemsBySectionAsync(sectionId));
    }

    [HttpGet("items/{id:guid}")]
    public async Task<ActionResult<MenuItemDto>> GetItem(Guid id)
    {
        var result = await _service.GetItemByIdAsync(id);
        if (result == null) return NotFound();
        return Ok(result);
    }

    [HttpPost("sections/{sectionId:guid}/items")]
    public async Task<ActionResult> CreateItem(Guid sectionId, [FromBody] MenuItemUpsertRequest request)
    {
        var id = await _service.CreateItemAsync(sectionId, request);
        return Ok(new { id });
    }

    [HttpPut("items/{id:guid}")]
    public async Task<ActionResult> UpdateItem(Guid id, [FromBody] MenuItemUpsertRequest request)
    {
        var ok = await _service.UpdateItemAsync(id, request);
        if (!ok) return NotFound();
        return NoContent();
    }

    [HttpDelete("items/{id:guid}")]
    public async Task<ActionResult> DeleteItem(Guid id)
    {
        var ok = await _service.DeleteItemAsync(id);
        if (!ok) return NotFound();
        return NoContent();
    }
}