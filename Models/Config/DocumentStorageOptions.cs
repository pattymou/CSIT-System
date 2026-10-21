namespace SIT.DepartmentSystem.Web.Models.Config;

public sealed class DocumentStorageOptions
{
    public const string SectionName = "DocumentStorage";

    public string RootPath { get; set; } = string.Empty;
    public long MaxFileSizeBytes { get; set; } = 200_000_000;
}
