namespace SIT.DepartmentSystem.Web.Services;

public sealed record VerificationApplicationValidationIssue(
    string AreaCode,
    string AreaLabel,
    string? FieldCode,
    string FieldLabel)
{
    public const string TestItemsAreaCode = "__TEST_ITEMS";
}

public sealed class VerificationApplicationValidationException(
    IReadOnlyList<VerificationApplicationValidationIssue> issues)
    : InvalidOperationException($"新版申請單尚未完整填寫：{string.Join("、", issues.Select(x => x.FieldLabel))}")
{
    public IReadOnlyList<VerificationApplicationValidationIssue> Issues { get; } = issues;
}
