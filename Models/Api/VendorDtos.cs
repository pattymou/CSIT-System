namespace SIT.DepartmentSystem.Web.Models.Api;

public class VendorListItemDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Aliases { get; set; }
    public string[] Types { get; set; } = [];
    public string? ContactPerson { get; set; }
    public string? Phone { get; set; }
    public bool IsActive { get; set; }
    public int EquipmentCount { get; set; }
}

public sealed class VendorDetailDto : VendorListItemDto
{
    public string? Website { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? Notes { get; set; }
    public string? RowVersion { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
    public List<VendorFileDto> Files { get; set; } = [];
}

public sealed class VendorUpsertRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Aliases { get; set; }
    public string[] Types { get; set; } = [];
    public string? Website { get; set; }
    public string? ContactPerson { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
    public string? RowVersion { get; set; }
}

public sealed class VendorOptionDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}

public sealed class VendorFileDto
{
    public Guid Id { get; set; }
    public Guid VendorId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string StoredFileName { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public long FileSize { get; set; }
    public string? Sha256 { get; set; }
    public string ArchiveStatus { get; set; } = string.Empty;
    public string? ArchiveError { get; set; }
    public string DownloadUrl { get; set; } = string.Empty;
    public bool IsImage { get; set; }
    public string UploadedBy { get; set; } = string.Empty;
    public DateTime UploadedAt { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public bool HasKmSchedule { get; set; }
    public bool KmEnabled { get; set; }
    public string? KmStatus { get; set; }
    public string? KmError { get; set; }
    public bool CanDelete { get; set; }
}

public sealed class VendorUploadResponseDto
{
    public string Status { get; set; } = string.Empty;
    public List<Guid> OperationIds { get; set; } = [];
}

public sealed class ManufacturerMatchReportItemDto
{
    public string Manufacturer { get; set; } = string.Empty;
    public int EquipmentCount { get; set; }
    public List<VendorOptionDto> CandidateVendors { get; set; } = [];
}
