namespace SIT.DepartmentSystem.Web.Models.Config;

public sealed class ApprovalStagingCleanupOptions
{
    public bool EnableNewSubmissionCleanup { get; set; }
    public DateTime? NewSubmissionNotBeforeUtc { get; set; }
    public bool EnableHistoricalCleanup { get; set; }
    public int BatchSize { get; set; } = 20;
}
