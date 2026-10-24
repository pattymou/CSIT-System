namespace SIT.DepartmentSystem.Web.Services;

public sealed record VerificationApplicationValidationIssue(
    string AreaCode,
    string AreaLabel,
    string? FieldCode,
    string FieldLabel)
{
    public const string TestItemsAreaCode = "__TEST_ITEMS";
    public const string DirectApprovalAreaCode = "__DIRECT_APPROVAL";
    public const string ExpectedEndDateFieldCode = "EXPECTED_END_DATE";
}

public sealed class VerificationApplicationValidationException : InvalidOperationException
{
    public VerificationApplicationValidationException(
        IReadOnlyList<VerificationApplicationValidationIssue> issues,
        string? message = null)
        : base(message ?? $"新版申請單尚未完整填寫：{string.Join("、", issues.Select(x => x.FieldLabel))}")
    {
        Issues = issues;
    }

    public IReadOnlyList<VerificationApplicationValidationIssue> Issues { get; }
}
