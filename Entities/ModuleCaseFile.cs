using System.ComponentModel.DataAnnotations.Schema;

namespace SIT.DepartmentSystem.Web.Entities;

public class ModuleCaseFile
{
    public Guid Id { get; set; }

    public Guid RecordId { get; set; }

    public Guid? CaseId { get; set; }

    public string CaseNo { get; set; } = string.Empty;

    public Guid? TaskId { get; set; }

    public string? TaskNo { get; set; }

    public string FileName { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    public string? ContentType { get; set; }

    public long FileSize { get; set; }

    public string? UploadEmp { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid DocumentId { get; set; }

    public int VersionNo { get; set; }

    public string FileKind { get; set; } = ModuleCaseFileKinds.Attachment;

    public string? ReviewStatus { get; set; }

    public DateTime? ReviewedAt { get; set; }

    public string? ReviewedBy { get; set; }

    public string? ReviewComment { get; set; }

    public bool IsFinal { get; set; }

    public DateTime? FinalizedAt { get; set; }

    public string? FinalizedBy { get; set; }

    public string? Sha256 { get; set; }

    public DateTime UpdatedAt { get; set; }

    public ModuleRecord Record { get; set; } = null!;

    public ModuleRecordCase? Case { get; set; }

    public ModuleRecordTask? Task { get; set; }

    // =========================
    // 🔥 RAW DATA 欄位（重點）
    // =========================

    [Column("nas_folder_path")]
    public string? NasFolderPath { get; set; }

    [Column("nas_file_path")]
    public string? NasFilePath { get; set; }

    [Column("raw_json_path")]
    public string? RawJsonPath { get; set; }

    [Column("is_raw_data_exported")]
    public bool IsRawDataExported { get; set; }

    [Column("raw_data_exported_at")]
    public DateTime? RawDataExportedAt { get; set; }

    [Column("raw_data_export_error")]
    public string? RawDataExportError { get; set; }
}

public static class ModuleCaseFileKinds
{
    public const string Attachment = "Attachment";
    public const string TestReport = "TestReport";
}

public static class TestReportReviewStatuses
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
}
