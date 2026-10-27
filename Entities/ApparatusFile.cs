namespace SIT.DepartmentSystem.Web.Entities;

public class ApparatusFile
{
    public Guid Id { get; set; }

    public string ApparatusId { get; set; } = string.Empty;
    public Apparatus Apparatus { get; set; } = null!;

    public string FileName { get; set; } = string.Empty;
    public string? OriginalFileName { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public long FileSize { get; set; }

    public string? UploadEmp { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? Sha256 { get; set; }
    public string? ArchiveStatus { get; set; }
    public string? ArchiveError { get; set; }
    public int ArchiveAttemptCount { get; set; }
    public DateTime? ArchiveNextRetryAt { get; set; }
    public DateTime? ArchiveUpdatedAt { get; set; }
    public string? MetadataJson { get; set; }
    public string? MetadataSource { get; set; }
    public string? MetadataVersion { get; set; }
    public string? MetadataVerificationStatus { get; set; }

    public string? NasFolderPath { get; set; }
    public string? NasFilePath { get; set; }
    public string? RawJsonPath { get; set; }
    public bool IsRawDataExported { get; set; }
    public DateTime? RawDataExportedAt { get; set; }
    public string? RawDataExportError { get; set; }
}
