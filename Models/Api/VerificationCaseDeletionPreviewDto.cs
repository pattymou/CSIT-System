namespace SIT.DepartmentSystem.Web.Models.Api;

public sealed class VerificationCaseDeletionPreviewDto
{
    public Guid ApplicationId { get; set; }
    public Guid RecordId { get; set; }
    public string ApplicationNo { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public bool CanExecute { get; set; }
    public string? DeletionStatus { get; set; }
    public IReadOnlyList<VerificationCaseDeletionInventoryItemDto> Items { get; set; } = [];
    public IReadOnlyList<string> BlockingReasons { get; set; } = [];
}

public sealed record VerificationCaseDeletionInventoryItemDto(
    string Kind, Guid Id, Guid? ParentId, Guid? DocumentId, int? Version,
    string Name, string? Status, string? StorageArea = null,
    bool? PhysicalExists = null, string? KmJobId = null);
