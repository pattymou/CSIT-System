namespace SIT.DepartmentSystem.Web.Services;

public static class DepartmentTeamRules
{
    public static string DisplayName(string? name, string? value) =>
        string.IsNullOrWhiteSpace(name) ? value?.Trim() ?? string.Empty : name.Trim();

    public static IReadOnlyList<TTeam> GetAllowedTeamsForDepartment<TTeam>(
        string? department,
        IEnumerable<TTeam> enabledTeams,
        Func<TTeam, string?> nameSelector,
        Func<TTeam, string?> valueSelector)
        => GetAllowedTeamsForWjScope(
            IsWjDepartment(department),
            enabledTeams,
            nameSelector,
            valueSelector);

    public static IReadOnlyList<TTeam> GetAllowedTeamsForLocation<TTeam>(
        string? location,
        IEnumerable<TTeam> enabledTeams,
        Func<TTeam, string?> nameSelector,
        Func<TTeam, string?> valueSelector)
    {
        if (!IsSupportedLocation(location))
            return [];

        return GetAllowedTeamsForWjScope(
            IsWjLocation(location),
            enabledTeams,
            nameSelector,
            valueSelector);
    }

    public static IReadOnlyList<TTeam> GetAllowedTeamsForWjScope<TTeam>(
        bool wantsWj,
        IEnumerable<TTeam> enabledTeams,
        Func<TTeam, string?> nameSelector,
        Func<TTeam, string?> valueSelector)
    {
        ArgumentNullException.ThrowIfNull(enabledTeams);
        ArgumentNullException.ThrowIfNull(nameSelector);
        ArgumentNullException.ThrowIfNull(valueSelector);

        var teams = enabledTeams.ToList();
        var wjTeams = teams
            .Where(team => IsWjTeam(nameSelector(team), valueSelector(team)))
            .ToList();

        if (wantsWj)
            return wjTeams.Count > 0 ? wjTeams : teams;

        return teams
            .Where(team => !IsWjTeam(nameSelector(team), valueSelector(team)))
            .ToList();
    }

    public static bool IsWjDepartment(string? department) =>
        department?.Trim().EndsWith("-WJ", StringComparison.OrdinalIgnoreCase) == true;

    public static bool IsWjLocation(string? location) =>
        string.Equals(location?.Trim(), "吳江", StringComparison.OrdinalIgnoreCase);

    public static bool IsSupportedLocation(string? location) =>
        IsWjLocation(location) ||
        string.Equals(location?.Trim(), "台北", StringComparison.OrdinalIgnoreCase);

    public static bool IsWjTeam(string? name, string? value) =>
        name?.Contains("WJ", StringComparison.OrdinalIgnoreCase) == true ||
        value?.Contains("WJ", StringComparison.OrdinalIgnoreCase) == true;
}
