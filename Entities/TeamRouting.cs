namespace SIT.DepartmentSystem.Web.Entities;

/// <summary>
/// System-wide routing master that assigns one leader to a Team option.
/// </summary>
public class TeamRouting
{
    public Guid Id { get; set; }
    public Guid TeamOptionId { get; set; }
    public string LeaderAccount { get; set; } = string.Empty;
    public string LeaderDisplayName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
