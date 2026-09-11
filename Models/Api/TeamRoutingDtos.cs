namespace SIT.DepartmentSystem.Web.Models.Api;

public class TeamRoutingUpsertRequest
{
    public Guid TeamOptionId { get; set; }
    public string LeaderAccount { get; set; } = string.Empty;
    public string LeaderDisplayName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
}

public class TeamRoutingDto : TeamRoutingUpsertRequest
{
    public Guid Id { get; set; }
    public string TeamCode { get; set; } = string.Empty;
    public string TeamName { get; set; } = string.Empty;
    public bool TeamIsEnabled { get; set; }
}

public sealed record TeamLeaderResolution(
    Guid TeamOptionId,
    string TeamCode,
    string TeamName,
    string LeaderAccount,
    string LeaderDisplayName);
