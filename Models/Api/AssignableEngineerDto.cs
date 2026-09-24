namespace SIT.DepartmentSystem.Web.Models.Api;

public sealed record AssignableEngineerDto(
    Guid IdentityUserId,
    string Account,
    string DisplayName,
    string? EmployeeNo);
