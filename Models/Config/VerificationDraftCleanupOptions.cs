namespace SIT.DepartmentSystem.Web.Models.Config;

public sealed class VerificationDraftCleanupOptions
{
    public const string SectionName = "VerificationDraftCleanup";

    public bool Enabled { get; set; }
    public bool DryRun { get; set; } = true;
    public bool DeletionConfirmed { get; set; }
    public int RetentionDays { get; set; } = 15;
    public int RunIntervalHours { get; set; } = 24;
    public string StatePath { get; set; } = string.Empty;
}
