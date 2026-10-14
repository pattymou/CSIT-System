using System.ComponentModel.DataAnnotations;

namespace SIT.DepartmentSystem.Web.Models;

public enum CsitProvisioningRole { RdApplicant, CsitStaff, Admin }
public enum ProvisionedDataAccessLevel { ReadOnly, Editor }
public enum ManagedAccountStatus { Active, Disabled }

public sealed class CreateInternalAccountForm
{
    [Required, StringLength(50)] public string EmployeeNo { get; set; } = string.Empty;
    [Required, StringLength(100)] public string Account { get; set; } = string.Empty;
    [Required, StringLength(200)] public string DisplayName { get; set; } = string.Empty;
    [Required, StringLength(100)] public string Department { get; set; } = string.Empty;
    [Required] public string TeamCode { get; set; } = string.Empty;
    [Required, StringLength(100)] public string Position { get; set; } = string.Empty;
    [Required, EmailAddress, RegularExpression(@"(?i)^[^@\s]+@askey\.com$",
        ErrorMessage = "Email 必須使用 @askey.com 網域。")]
    public string Email { get; set; } = string.Empty;
    [StringLength(30)] public string? Extension { get; set; }
    [Required] public CsitProvisioningRole? CsitRole { get; set; }
    [Required] public ProvisionedDataAccessLevel? DataAccessLevel { get; set; }
}

public sealed record ProvisionableTeam(string Code, string Name);

public sealed record ProvisioningActorContext(
    Guid IdentityUserId,
    string Account,
    bool IsAdmin,
    IReadOnlyList<ProvisionableTeam> Teams);

public sealed record InternalAccountProvisioningRequest(
    string EmployeeNo,
    string Account,
    string DisplayName,
    string Department,
    string TeamCode,
    string Position,
    string Email,
    string? Extension,
    CsitProvisioningRole CsitRole,
    ProvisionedDataAccessLevel DataAccessLevel,
    Guid CreatedByIdentityUserId,
    Guid CorrelationId,
    string IdempotencyKey);

public sealed record InternalAccountProvisioningResult(
    Guid IdentityUserId,
    string Account,
    string? TemporaryPassword,
    bool MustChangePassword,
    DateTimeOffset? TemporaryPasswordExpiresAt,
    DateTimeOffset CreatedAt,
    bool IsIdempotentReplay);

public sealed record AccountProvisioningOptions(
    ProvisioningActorContext Actor,
    IReadOnlyList<string> Departments);

public sealed record SharedIdentityAccountListItem(
    Guid IdentityUserId,
    string? EmployeeNo,
    string Account,
    string DisplayName,
    string Department,
    string? TeamCode,
    string? Position,
    string? Extension,
    string Email,
    string CsitRole,
    string DataAccessLevel,
    string AccountStatus,
    string SystemAccessStatus,
    DateTimeOffset CreatedAt,
    Guid? CreatedByIdentityUserId,
    string? CreatedByAccount);

public sealed record ManagedAccountListItem(
    Guid IdentityUserId,
    string? EmployeeNo,
    string Account,
    string DisplayName,
    string Department,
    string? TeamCode,
    string? Position,
    string? Extension,
    string Email,
    string CsitRole,
    string DataAccessLevel,
    string AccountStatus,
    string SystemAccessStatus,
    DateTimeOffset CreatedAt,
    Guid? CreatedByIdentityUserId,
    string? CreatedByAccount,
    bool IsTeamLeader);

public sealed class EditInternalAccountForm
{
    public Guid IdentityUserId { get; set; }
    [Required, StringLength(100)] public string Account { get; set; } = string.Empty;
    [Required, StringLength(50)] public string EmployeeNo { get; set; } = string.Empty;
    [Required, StringLength(200)] public string DisplayName { get; set; } = string.Empty;
    [Required, StringLength(100)] public string Department { get; set; } = string.Empty;
    [Required] public string TeamCode { get; set; } = string.Empty;
    [Required, StringLength(100)] public string Position { get; set; } = string.Empty;
    [Required, EmailAddress, RegularExpression(@"(?i)^[^@\s]+@askey\.com$",
        ErrorMessage = "Email 必須使用 @askey.com 網域。")]
    public string Email { get; set; } = string.Empty;
    [StringLength(30)] public string? Extension { get; set; }
    [Required] public CsitProvisioningRole? CsitRole { get; set; }
    [Required] public ProvisionedDataAccessLevel? DataAccessLevel { get; set; }
    [Required] public ManagedAccountStatus? AccountStatus { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedByAccount { get; set; }
}

public sealed record InternalAccountManagementRequest(
    string EmployeeNo,
    string DisplayName,
    string Department,
    string TeamCode,
    string Position,
    string Email,
    string? Extension,
    CsitProvisioningRole CsitRole,
    ProvisionedDataAccessLevel DataAccessLevel,
    ManagedAccountStatus AccountStatus,
    Guid ActorIdentityUserId,
    Guid CorrelationId);

public sealed record InternalAccountManagementResult(
    Guid IdentityUserId,
    string Account,
    long SecurityVersion,
    long AuthorizationVersion);

public sealed record TemporaryPasswordReissueRequest(
    Guid CreatedByIdentityUserId,
    Guid CorrelationId,
    string IdempotencyKey);

public sealed record AccountDeprovisionRequest(
    Guid ActorIdentityUserId,
    Guid CorrelationId);
