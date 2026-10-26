using SIT.DepartmentSystem.Web.Models.Api;

namespace SIT.DepartmentSystem.Web.Services.ValidationRequests;

public static class ValidationRequestPresentation
{
    public static readonly IReadOnlyDictionary<string, string> FieldTypeLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Text"] = "文字",
        ["TextArea"] = "多行文字",
        ["SensitiveText"] = "密碼/敏感文字",
        ["Number"] = "數字",
        ["Dropdown"] = "下拉選單",
        ["SingleSelect"] = "單選",
        ["Checkbox"] = "勾選",
        ["MultiSelect"] = "複選",
        ["Attachment"] = "附件",
        ["Group"] = "欄位群組",
        ["RepeatableText"] = "可新增多筆文字",
        ["RepeatableGroup"] = "可新增多組資料",
        ["Lookup"] = "系統資料選擇"
    };

    public static string FieldType(string value) => FieldTypeLabels.GetValueOrDefault(value, value);
    public static string TemplateStatus(string value) => value switch { "Draft" => "草稿", "Published" => "已發布", _ => value };
    public static string SectionLabel(ValidationSectionDto section) => section.Code == "COMMON_PRODUCT_INFO" ? "產品資訊" : section.DisplayLabel;
    public static string Required(bool value) => value ? "必填" : "選填";
    public static string Enabled(bool value) => value ? "啟用" : "停用";
    public static string CategoryLabel(ValidationTestCategoryDto category) => $"{category.Code} {category.DisplayLabel}";

    public static bool IsVisible(ValidationFieldDto field, IReadOnlyDictionary<string, List<string>> values) =>
        field.Conditions.Where(x => x.Action == "Visible").All(condition =>
        {
            values.TryGetValue(condition.SourceFieldCode, out var actual);
            actual ??= [];
            var expected = condition.ExpectedValue ?? string.Empty;
            return condition.Operator switch
            {
                "Equals" => actual.Any(x => string.Equals(x, expected, StringComparison.OrdinalIgnoreCase)),
                "NotEquals" => actual.All(x => !string.Equals(x, expected, StringComparison.OrdinalIgnoreCase)),
                "Contains" => actual.Contains(expected, StringComparer.OrdinalIgnoreCase),
                "NotContains" => !actual.Contains(expected, StringComparer.OrdinalIgnoreCase),
                "IsTrue" => actual.Any(x => string.Equals(x, "true", StringComparison.OrdinalIgnoreCase)),
                "IsFalse" => actual.All(x => !string.Equals(x, "true", StringComparison.OrdinalIgnoreCase)),
                _ => false
            };
        });

    public static IReadOnlyList<ValidationFieldGroup> Groups(ValidationSectionDto section)
    {
        var fields = section.Fields.OrderBy(x => x.DisplayOrder).ToList();
        return section.Code switch
        {
            "COMMON_PRODUCT_INFO" =>
            [
                new("A. 產品專案資訊", fields.Where(x => x.DisplayOrder <= 110).ToList()),
                new("B. 產品軟硬體版本資訊", fields.Where(x => x.DisplayOrder is > 110 and < 160).ToList()),
                new("C. 其他資訊", fields.Where(x => x.DisplayOrder is >= 160 and < 170).ToList()),
                new("D. 產品登入資訊", fields.Where(x => x.DisplayOrder >= 170).ToList())
            ],
            "TYPE_WIFI" => Split(fields, 50),
            "TYPE_CELLULAR_UE" => Split(fields, 20),
            "TYPE_SMALLCELL" => Split(fields, 20),
            _ => [new("欄位", fields)]
        };
    }

    private static IReadOnlyList<ValidationFieldGroup> Split(IReadOnlyList<ValidationFieldDto> fields, int informationEnd) =>
    [
        new("A. Information", fields.Where(x => x.DisplayOrder <= informationEnd).ToList()),
        new("B. Capability", fields.Where(x => x.DisplayOrder > informationEnd).ToList())
    ];
}

public sealed record ValidationFieldGroup(string Label, IReadOnlyList<ValidationFieldDto> Fields);
