namespace SIT.DepartmentSystem.Web.Entities;

public sealed class Vendor
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public string? Aliases { get; set; }
    public string[] Types { get; set; } = [];
    public string? Website { get; set; }
    public string? ContactPerson { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
    public string StorageFolderName { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
    public uint Xmin { get; set; }

    public List<Apparatus> Apparatuses { get; set; } = [];
    public List<VendorFile> Files { get; set; } = [];
}
