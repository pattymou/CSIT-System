namespace SIT.DepartmentSystem.Web.Services;

public static class ModuleDateRangeValidation
{
    public const string DatePairRequiredMessage = "開始日期與預計完成日期必須同時設定。";
    public const string StartAfterEndMessage = "開始日期不可晚於預計完成日期。";
    public const string ParentDatesRequiredMessage = "請先設定主單的開始日期與預計完成日期。";

    public static string? GetTaskError(
        DateOnly? taskStartDate,
        DateOnly? taskExpectedEndDate,
        DateOnly? recordStartDate,
        DateOnly? recordExpectedEndDate)
    {
        if (taskStartDate.HasValue != taskExpectedEndDate.HasValue)
        {
            return DatePairRequiredMessage;
        }

        if (!taskStartDate.HasValue)
        {
            return null;
        }

        if (taskStartDate.Value > taskExpectedEndDate!.Value)
        {
            return StartAfterEndMessage;
        }

        if (!recordStartDate.HasValue || !recordExpectedEndDate.HasValue)
        {
            return ParentDatesRequiredMessage;
        }

        if (taskStartDate.Value < recordStartDate.Value
            || taskExpectedEndDate.Value > recordExpectedEndDate.Value)
        {
            return $"子任務日期必須介於主單日期 {Format(recordStartDate.Value)} ～ {Format(recordExpectedEndDate.Value)} 之間。";
        }

        return null;
    }

    public static string? GetRecordError(
        DateOnly? recordStartDate,
        DateOnly? recordExpectedEndDate,
        DateOnly? earliestTaskStartDate = null,
        DateOnly? latestTaskExpectedEndDate = null)
    {
        if (recordStartDate.HasValue != recordExpectedEndDate.HasValue)
        {
            return DatePairRequiredMessage;
        }

        if (recordStartDate.HasValue && recordStartDate.Value > recordExpectedEndDate!.Value)
        {
            return StartAfterEndMessage;
        }

        if (!earliestTaskStartDate.HasValue || !latestTaskExpectedEndDate.HasValue)
        {
            return null;
        }

        if (!recordStartDate.HasValue
            || recordStartDate.Value > earliestTaskStartDate.Value
            || recordExpectedEndDate!.Value < latestTaskExpectedEndDate.Value)
        {
            return $"主單日期必須涵蓋所有子任務日期，目前子任務日期範圍為 {Format(earliestTaskStartDate.Value)} ～ {Format(latestTaskExpectedEndDate.Value)}。";
        }

        return null;
    }

    private static string Format(DateOnly date) => date.ToString("yyyy/MM/dd");
}
