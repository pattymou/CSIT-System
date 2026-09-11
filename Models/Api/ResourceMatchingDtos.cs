namespace SIT.DepartmentSystem.Web.Models.Api;

public sealed class ApparatusResourceCapabilityInput
{
    public string ResourceType { get; set; } = string.Empty;
    public string? CapabilityTag { get; set; }
}

public sealed class ReplaceApparatusResourceCapabilitiesRequest
{
    public List<ApparatusResourceCapabilityInput> Mappings { get; set; } = new();
}

public sealed class ApparatusResourceCapabilityDto
{
    public Guid Id { get; set; }
    public string ApparatusId { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public string? CapabilityTag { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class ResourceMatchingCatalogValueDto
{
    public string ResourceType { get; set; } = string.Empty;
    public string? CapabilityTag { get; set; }
}
