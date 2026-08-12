using SIT.DepartmentSystem.Web.Models.Api;

namespace SIT.DepartmentSystem.Web.Services.Interfaces;

public interface IMenuManagementService
{
    Task<List<MenuSectionDto>> GetSectionsAsync();
    Task<List<MenuItemDto>> GetItemsBySectionAsync(Guid sectionId);
    Task<MenuItemDto?> GetItemByIdAsync(Guid id);
    Task<Guid> CreateItemAsync(Guid sectionId, MenuItemUpsertRequest request);
    Task<bool> UpdateItemAsync(Guid id, MenuItemUpsertRequest request);
    Task<bool> DeleteItemAsync(Guid id);
}