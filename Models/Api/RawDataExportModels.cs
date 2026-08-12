namespace SIT.DepartmentSystem.Web.Models.Api;

public class RawDataExportRequest
{
    public string SourceSystem { get; set; } = "CSIT";

    public string SourceType { get; set; } = string.Empty;

    public string? ModuleCode { get; set; }

    public string? RecordId { get; set; }

    public string? RecordNo { get; set; }

    public string? CaseId { get; set; }

    public string? CaseNo { get; set; }

    public string? TaskId { get; set; }

    public string? TaskNo { get; set; }

    public string FileId { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string LocalFilePath { get; set; } = string.Empty;

    public string? ContentType { get; set; }

    public long FileSize { get; set; }

    public string? UploadEmp { get; set; }

    public DateTime UploadedAt { get; set; }
}

public class RawDataLatestPackageRequest
{
    public string SourceSystem { get; set; } = "CSIT";

    public string SourceType { get; set; } = string.Empty;

    public string ModuleCode { get; set; } = "general";

    public string EntityId { get; set; } = string.Empty;

    public string? LocalRootFolder { get; set; }

    public object Metadata { get; set; } = new();

    public List<RawDataLatestPackageFile> Files { get; set; } = new();
}

public class RawDataLatestPackageFile
{
    public string FileId { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string LocalFilePath { get; set; } = string.Empty;

    public string? ContentType { get; set; }

    public long FileSize { get; set; }

    public string? UploadEmp { get; set; }

    public DateTime UploadedAt { get; set; }
}

public class RawDataExportResult
{
    public bool Success { get; set; }

    public string? NasFolderPath { get; set; }

    public string? NasFilePath { get; set; }

    public string? RawJsonPath { get; set; }

    public string? ErrorMessage { get; set; }
}