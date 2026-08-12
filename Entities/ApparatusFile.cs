namespace SIT.DepartmentSystem.Web.Entities;

public class ApparatusFile
{
    public Guid Id { get; set; }

    public string ApparatusId { get; set; } = string.Empty;
    public Apparatus Apparatus { get; set; } = null!;

    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public long FileSize { get; set; }

    public string? UploadEmp { get; set; }
    public DateTime CreatedAt { get; set; }

    public string? NasFolderPath { get; set; }
    public string? NasFilePath { get; set; }
    public string? RawJsonPath { get; set; }
    public bool IsRawDataExported { get; set; }
    public DateTime? RawDataExportedAt { get; set; }
    public string? RawDataExportError { get; set; }
}