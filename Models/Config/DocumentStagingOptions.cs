namespace SIT.DepartmentSystem.Web.Models.Config;

public sealed class DocumentStagingOptions
{
    public const string SectionName = "DocumentStaging";

    public string RootPath { get; set; } = string.Empty;
}
