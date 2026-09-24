namespace SIT.DepartmentSystem.Web.Models.Api;

public sealed record LabOwnerOptionDto(
    Guid IdentityUserId,
    string Account,
    string DisplayName,
    string? EmployeeNo);
