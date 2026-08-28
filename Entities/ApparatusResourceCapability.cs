namespace SIT.DepartmentSystem.Web.Entities;

public sealed class ApparatusResourceCapability
{
    public Guid Id { get; set; }
    public string ApparatusId { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public string? CapabilityTag { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Apparatus Apparatus { get; set; } = null!;
}
