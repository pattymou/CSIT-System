namespace SIT.DepartmentSystem.Web.Services;

// These values must be built by trusted server-side identity code, never from application content input.
public sealed record ApplicantSnapshot(
    string ApplicantAccount,
    string ApplicantName,
    string ApplicantEmail,
    string Department,
    string? ApplicantExtension);

// The target module must come from restricted server-side routing/configuration.
public sealed record VerificationApplicationTarget(string ModuleCode);
