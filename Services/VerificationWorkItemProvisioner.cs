using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;

namespace SIT.DepartmentSystem.Web.Services;

public sealed class VerificationWorkItemProvisioner(AppDbContext db)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task EnsureAsync(
        VerificationApplication application,
        ModuleRecord record,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(application.ValidationFormPayloadJson)) return;

        var request = JsonSerializer.Deserialize<ValidationRequestSubmissionDto>(
            application.ValidationFormPayloadJson, JsonOptions)
            ?? throw new InvalidOperationException("The submitted validation form payload is invalid.");
        var snapshot = request.FormSnapshot
            ?? throw new InvalidOperationException("The submitted validation form snapshot is missing.");
        if (request.TemplateVersionId != snapshot.TemplateVersionId ||
            request.TemplateVersion != snapshot.TemplateVersion ||
            !string.Equals(request.ProductTypeCode, snapshot.ProductTypeCode, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The submitted validation form does not match its immutable snapshot.");

        var selected = request.SelectedTestItemCodes
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0)
            throw new InvalidOperationException("The submitted validation request has no selected test items.");

        var available = snapshot.TestCategories
            .SelectMany(category => category.TestItems.Select(item => new { Category = category, Item = item }))
            .Where(x => selected.Contains(x.Item.Code))
            .ToList();
        var duplicateCodes = available.GroupBy(x => x.Item.Code, StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Count() != 1).Select(x => x.Key).ToList();
        var found = available.Select(x => x.Item.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = selected.Where(x => !found.Contains(x)).ToList();
        if (missing.Count > 0 || duplicateCodes.Count > 0)
            throw new InvalidOperationException("Selected test items do not map uniquely to the submitted form snapshot.");

        var existingCases = await db.ModuleRecordCases
            .Include(x => x.Tasks)
            .Where(x => x.RecordId == record.Id && x.SourceTestCategoryCode != null)
            .ToListAsync(cancellationToken);

        foreach (var categoryGroup in available
                     .GroupBy(x => x.Category.Code, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(x => x.First().Category.DisplayOrder))
        {
            var category = categoryGroup.First().Category;
            var recordCase = existingCases.SingleOrDefault(x => string.Equals(
                x.SourceTestCategoryCode, category.Code, StringComparison.OrdinalIgnoreCase));
            if (recordCase is null)
            {
                recordCase = new ModuleRecordCase
                {
                    Id = Guid.NewGuid(),
                    RecordId = record.Id,
                    CaseNo = $"{application.ApplicationNo}-{category.Code}",
                    Name = category.DisplayLabel,
                    Status = "Open",
                    SortOrder = category.DisplayOrder,
                    SourceTestCategoryCode = category.Code.Trim().ToUpperInvariant(),
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.ModuleRecordCases.Add(recordCase);
                existingCases.Add(recordCase);
            }

            foreach (var entry in categoryGroup.OrderBy(x => x.Item.DisplayOrder))
            {
                if (recordCase.Tasks.Any(x => string.Equals(
                        x.SourceTestItemCode, entry.Item.Code, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var parameters = request.TestParameterValues.FirstOrDefault(x => string.Equals(
                    x.Key, entry.Item.Code, StringComparison.OrdinalIgnoreCase)).Value;
                var task = new ModuleRecordTask
                {
                    Id = Guid.NewGuid(),
                    CaseId = recordCase.Id,
                    TaskNo = $"{application.ApplicationNo}-{entry.Item.Code}",
                    Name = entry.Item.DisplayLabel,
                    Status = "Open",
                    Progress = 0,
                    ExpectedEndDate = record.ExpectedEndDate,
                    ModelName = record.CustomerProductName,
                    Note = entry.Item.Description,
                    SourceTestItemCode = entry.Item.Code.Trim().ToUpperInvariant(),
                    SourceTestParametersJson = entry.Item.Parameters.Count == 0 && parameters is null
                        ? null
                        : JsonSerializer.Serialize(new { definitions = entry.Item.Parameters, values = parameters }, JsonOptions),
                    CreatedAt = now,
                    UpdatedAt = now
                };
                recordCase.Tasks.Add(task);
                db.ModuleRecordTasks.Add(task);
            }
        }
    }
}
