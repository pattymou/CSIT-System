using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;

namespace SIT.DepartmentSystem.Web.Services.ValidationRequests;

public interface IValidationRequestConfigurationService
{
    Task<ValidationRequestConfigurationDto> GetConfigurationAsync(CancellationToken cancellationToken = default);
    Task<ValidationRequestPreviewDto> GetPreviewAsync(string productTypeCode, CancellationToken cancellationToken = default);
    Task<Guid> SaveProductTypeAsync(Guid? id, SaveValidationProductTypeRequest request, CancellationToken cancellationToken = default);
    Task SetSectionMappingsAsync(Guid productTypeId, IReadOnlyList<Guid> sectionIds, CancellationToken cancellationToken = default);
    Task SetCategoryMappingsAsync(Guid productTypeId, IReadOnlyList<Guid> categoryIds, CancellationToken cancellationToken = default);
    Task<Guid> SaveFieldAsync(Guid? id, SaveValidationFieldRequest request, CancellationToken cancellationToken = default);
    Task DeleteFieldAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Guid> SaveOptionAsync(Guid fieldId, Guid? id, SaveValidationOptionRequest request, CancellationToken cancellationToken = default);
    Task DeleteOptionAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Guid> SaveConditionAsync(Guid fieldId, Guid? id, SaveValidationConditionRequest request, CancellationToken cancellationToken = default);
    Task DeleteConditionAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Guid> SaveTestItemAsync(Guid? id, SaveValidationTestItemRequest request, CancellationToken cancellationToken = default);
    Task DeleteTestItemAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Guid> SaveParameterAsync(Guid itemId, Guid? id, SaveValidationParameterRequest request, CancellationToken cancellationToken = default);
    Task PublishAsync(Guid versionId, CancellationToken cancellationToken = default);
    Task<Guid> CloneDraftAsync(Guid versionId, CancellationToken cancellationToken = default);
}

public sealed class ValidationRequestConfigurationService(IDbContextFactory<AppDbContext> dbFactory)
    : IValidationRequestConfigurationService
{
    public async Task<ValidationRequestConfigurationDto> GetConfigurationAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var template = await db.ValidationFormTemplates.AsNoTracking().SingleAsync(x => x.Code == "VALIDATION_TEST_REQUEST", ct);
        var versions = await db.ValidationFormTemplateVersions.AsNoTracking().Where(x => x.TemplateId == template.Id)
            .OrderByDescending(x => x.VersionNumber).ToListAsync(ct);
        var version = versions.FirstOrDefault(x => x.Status == ValidationTemplateStatuses.Draft) ??
                      versions.First(x => x.Status == ValidationTemplateStatuses.Published);
        var products = await db.ValidationProductTypes.AsNoTracking().OrderBy(x => x.DisplayOrder).ToListAsync(ct);
        var sectionMappings = await db.ValidationProductTypeSectionMappings.AsNoTracking()
            .Where(x => x.SectionDefinition.TemplateVersionId == version.Id).ToListAsync(ct);
        var categoryMappings = await db.ValidationProductTypeTestCategoryMappings.AsNoTracking()
            .Where(x => x.TestItemCategory.TemplateVersionId == version.Id).ToListAsync(ct);
        var sections = await LoadSections(db, version.Id, ct);
        var categories = await LoadCategories(db, version.Id, ct);
        return new(template.Code, template.DisplayName, Version(version), versions.Select(Version).ToList(),
            products.Select(p => new ValidationProductTypeDto(p.Id, p.Code, p.DisplayLabel, p.IsEnabled, p.DisplayOrder,
                sectionMappings.Where(x => x.ProductTypeId == p.Id).Select(x => x.SectionDefinitionId).ToList(),
                categoryMappings.Where(x => x.ProductTypeId == p.Id).Select(x => x.TestItemCategoryId).ToList())).ToList(),
            sections, categories, ValidationFieldTypes.All);
    }

    public async Task<ValidationRequestPreviewDto> GetPreviewAsync(string productTypeCode, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var template = await db.ValidationFormTemplates.AsNoTracking().SingleAsync(x => x.Code == "VALIDATION_TEST_REQUEST", ct);
        var version = await db.ValidationFormTemplateVersions.AsNoTracking().Where(x => x.TemplateId == template.Id)
            .OrderByDescending(x => x.Status == ValidationTemplateStatuses.Published)
            .ThenByDescending(x => x.VersionNumber).FirstAsync(ct);
        var product = await db.ValidationProductTypes.AsNoTracking().SingleAsync(x => x.Code == productTypeCode && x.IsEnabled, ct);
        var sectionIds = await db.ValidationProductTypeSectionMappings.AsNoTracking().Where(x => x.ProductTypeId == product.Id && x.SectionDefinition.TemplateVersionId == version.Id)
            .Select(x => x.SectionDefinitionId).ToListAsync(ct);
        var categoryIds = await db.ValidationProductTypeTestCategoryMappings.AsNoTracking().Where(x => x.ProductTypeId == product.Id && x.TestItemCategory.TemplateVersionId == version.Id)
            .Select(x => x.TestItemCategoryId).ToListAsync(ct);
        var sections = (await LoadSections(db, version.Id, ct)).Where(x => x.AlwaysVisible || sectionIds.Contains(x.Id)).ToList();
        var categories = (await LoadCategories(db, version.Id, ct)).Where(x => x.AlwaysVisible || categoryIds.Contains(x.Id)).ToList();
        return new(template.Code, version.VersionNumber, product.Code, product.DisplayLabel, sections, categories);
    }

    public async Task<Guid> SaveProductTypeAsync(Guid? id, SaveValidationProductTypeRequest request, CancellationToken ct = default)
    {
        Require(request.Code, nameof(request.Code)); Require(request.DisplayLabel, nameof(request.DisplayLabel));
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = id.HasValue ? await db.ValidationProductTypes.SingleAsync(x => x.Id == id, ct) : new ValidationProductType { Id = Guid.NewGuid(), CreatedAt = DateTime.UtcNow };
        if (!id.HasValue) db.ValidationProductTypes.Add(item);
        item.Code = request.Code.Trim().ToUpperInvariant(); item.DisplayLabel = request.DisplayLabel.Trim();
        item.IsEnabled = request.IsEnabled; item.DisplayOrder = request.DisplayOrder; item.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct); return item.Id;
    }

    public Task SetSectionMappingsAsync(Guid productTypeId, IReadOnlyList<Guid> ids, CancellationToken ct = default) =>
        SetMappings(productTypeId, ids, true, ct);
    public Task SetCategoryMappingsAsync(Guid productTypeId, IReadOnlyList<Guid> ids, CancellationToken ct = default) =>
        SetMappings(productTypeId, ids, false, ct);

    private async Task SetMappings(Guid productId, IReadOnlyList<Guid> ids, bool sections, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var versionId = await db.ValidationFormTemplateVersions
            .Where(x => x.Template.Code == "VALIDATION_TEST_REQUEST" && x.Status == ValidationTemplateStatuses.Draft)
            .OrderByDescending(x => x.VersionNumber).Select(x => x.Id).FirstOrDefaultAsync(ct);
        await RequireDraft(db, versionId, ct);
        if (sections)
        {
            if (await db.ValidationSectionDefinitions.AnyAsync(x => ids.Contains(x.Id) && x.TemplateVersionId != versionId, ct)) throw new InvalidOperationException("Mappings must belong to the active Draft version.");
            db.ValidationProductTypeSectionMappings.RemoveRange(db.ValidationProductTypeSectionMappings.Where(x => x.ProductTypeId == productId && x.SectionDefinition.TemplateVersionId == versionId));
            db.ValidationProductTypeSectionMappings.AddRange(ids.Select(x => new ValidationProductTypeSectionMapping { ProductTypeId = productId, SectionDefinitionId = x }));
        }
        else
        {
            if (await db.ValidationTestItemCategories.AnyAsync(x => ids.Contains(x.Id) && x.TemplateVersionId != versionId, ct)) throw new InvalidOperationException("Mappings must belong to the active Draft version.");
            db.ValidationProductTypeTestCategoryMappings.RemoveRange(db.ValidationProductTypeTestCategoryMappings.Where(x => x.ProductTypeId == productId && x.TestItemCategory.TemplateVersionId == versionId));
            db.ValidationProductTypeTestCategoryMappings.AddRange(ids.Select(x => new ValidationProductTypeTestCategoryMapping { ProductTypeId = productId, TestItemCategoryId = x }));
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<Guid> SaveFieldAsync(Guid? id, SaveValidationFieldRequest request, CancellationToken ct = default)
    {
        Require(request.Code, nameof(request.Code)); Require(request.DisplayLabel, nameof(request.DisplayLabel));
        if (!ValidationFieldTypes.All.Contains(request.FieldType)) throw new ArgumentException("Unsupported field type.");
        await using var db = await dbFactory.CreateDbContextAsync(ct); await RequireDraftBySection(db, request.SectionId, ct);
        var item = id.HasValue ? await db.ValidationFieldDefinitions.SingleAsync(x => x.Id == id, ct) : new ValidationFieldDefinition { Id = Guid.NewGuid() };
        if (item.WasPublished) throw new InvalidOperationException("Published fields are immutable; clone a new Draft version.");
        if (!id.HasValue) db.ValidationFieldDefinitions.Add(item);
        item.SectionDefinitionId = request.SectionId; item.ParentFieldDefinitionId = request.ParentFieldId;
        item.Code = request.Code.Trim().ToUpperInvariant(); item.DisplayLabel = request.DisplayLabel.Trim(); item.HelpText = request.HelpText;
        item.DesignNote = request.DesignNote; item.FieldType = request.FieldType; item.IsRequired = request.IsRequired;
        item.IsEnabled = request.IsEnabled; item.DisplayOrder = request.DisplayOrder; item.IsSensitive = request.IsSensitive || request.FieldType == "SensitiveText";
        item.DataSource = request.DataSource; item.ValidationJson = request.ValidationJson;
        await db.SaveChangesAsync(ct); return item.Id;
    }

    public async Task DeleteFieldAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.ValidationFieldDefinitions.Include(x => x.SectionDefinition).Include(x => x.Children).SingleAsync(x => x.Id == id, ct);
        await RequireDraft(db, item.SectionDefinition.TemplateVersionId, ct);
        if (item.WasPublished) throw new InvalidOperationException("Published fields cannot be physically deleted.");
        if (item.Children.Count != 0) throw new InvalidOperationException("Delete child fields first.");
        db.Remove(item); await db.SaveChangesAsync(ct);
    }

    public async Task<Guid> SaveOptionAsync(Guid fieldId, Guid? id, SaveValidationOptionRequest request, CancellationToken ct = default)
    {
        Require(request.ValueCode, nameof(request.ValueCode)); Require(request.DisplayLabel, nameof(request.DisplayLabel));
        await using var db = await dbFactory.CreateDbContextAsync(ct); await RequireDraftByField(db, fieldId, ct);
        var item = id.HasValue ? await db.ValidationFieldOptions.SingleAsync(x => x.Id == id, ct) : new ValidationFieldOption { Id = Guid.NewGuid(), FieldDefinitionId = fieldId };
        if (item.WasPublished) throw new InvalidOperationException("Published options are immutable."); if (!id.HasValue) db.Add(item);
        item.ValueCode = request.ValueCode.Trim().ToUpperInvariant(); item.DisplayLabel = request.DisplayLabel.Trim(); item.IsEnabled = request.IsEnabled; item.DisplayOrder = request.DisplayOrder;
        await db.SaveChangesAsync(ct); return item.Id;
    }

    public async Task DeleteOptionAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct); var item = await db.ValidationFieldOptions.SingleAsync(x => x.Id == id, ct);
        await RequireDraftByField(db, item.FieldDefinitionId, ct); if (item.WasPublished) throw new InvalidOperationException("Published options cannot be physically deleted.");
        db.Remove(item); await db.SaveChangesAsync(ct);
    }

    public async Task<Guid> SaveConditionAsync(Guid fieldId, Guid? id, SaveValidationConditionRequest request, CancellationToken ct = default)
    {
        if (!ValidationConditionOperators.All.Contains(request.Operator) || !ValidationConditionActions.All.Contains(request.Action)) throw new ArgumentException("Unsupported condition.");
        await using var db = await dbFactory.CreateDbContextAsync(ct); await RequireDraftByField(db, fieldId, ct);
        var item = id.HasValue ? await db.ValidationFieldConditions.SingleAsync(x => x.Id == id, ct) : new ValidationFieldCondition { Id = Guid.NewGuid(), TargetFieldDefinitionId = fieldId };
        if (!id.HasValue) db.Add(item); item.SourceFieldCode = request.SourceFieldCode.Trim().ToUpperInvariant(); item.Operator = request.Operator; item.ExpectedValue = request.ExpectedValue; item.Action = request.Action;
        await db.SaveChangesAsync(ct); return item.Id;
    }

    public async Task DeleteConditionAsync(Guid id, CancellationToken ct = default)
    { await using var db = await dbFactory.CreateDbContextAsync(ct); var item = await db.ValidationFieldConditions.SingleAsync(x => x.Id == id, ct); await RequireDraftByField(db, item.TargetFieldDefinitionId, ct); db.Remove(item); await db.SaveChangesAsync(ct); }

    public async Task<Guid> SaveTestItemAsync(Guid? id, SaveValidationTestItemRequest request, CancellationToken ct = default)
    {
        Require(request.Code, nameof(request.Code)); Require(request.DisplayLabel, nameof(request.DisplayLabel));
        await using var db = await dbFactory.CreateDbContextAsync(ct); await RequireDraftByCategory(db, request.CategoryId, ct);
        var item = id.HasValue ? await db.ValidationTestItemDefinitions.SingleAsync(x => x.Id == id, ct) : new ValidationTestItemDefinition { Id = Guid.NewGuid() };
        if (item.WasPublished) throw new InvalidOperationException("Published test items are immutable; clone a new Draft version."); if (!id.HasValue) db.Add(item);
        item.TestItemCategoryId = request.CategoryId; item.Code = request.Code.Trim().ToUpperInvariant(); item.DisplayLabel = request.DisplayLabel.Trim();
        item.Description = request.Description; item.DesignNote = request.DesignNote; item.IsMandatory = request.IsMandatory;
        item.IsEnabled = request.IsEnabled; item.DisplayOrder = request.DisplayOrder;
        await db.SaveChangesAsync(ct); return item.Id;
    }

    public async Task DeleteTestItemAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.ValidationTestItemDefinitions.Include(x => x.TestItemCategory).SingleAsync(x => x.Id == id, ct);
        await RequireDraft(db, item.TestItemCategory.TemplateVersionId, ct);
        if (item.WasPublished) throw new InvalidOperationException("Published or historically referenced test items cannot be physically deleted.");
        db.Remove(item); await db.SaveChangesAsync(ct);
    }

    public async Task<Guid> SaveParameterAsync(Guid itemId, Guid? id, SaveValidationParameterRequest request, CancellationToken ct = default)
    {
        if (!ValidationFieldTypes.All.Contains(request.FieldType) || request.FieldType is "SensitiveText" or "Attachment" or "Lookup" or "RepeatableText") throw new ArgumentException("Unsupported test item parameter type.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var categoryId = await db.ValidationTestItemDefinitions.Where(x => x.Id == itemId).Select(x => x.TestItemCategoryId).SingleAsync(ct); await RequireDraftByCategory(db, categoryId, ct);
        var item = id.HasValue ? await db.ValidationTestItemParameterDefinitions.SingleAsync(x => x.Id == id, ct) : new ValidationTestItemParameterDefinition { Id = Guid.NewGuid(), TestItemDefinitionId = itemId };
        if (item.WasPublished) throw new InvalidOperationException("Published parameters are immutable."); if (!id.HasValue) db.Add(item);
        item.ParentParameterDefinitionId = request.ParentParameterId; item.Code = request.Code.Trim().ToUpperInvariant(); item.DisplayLabel = request.DisplayLabel.Trim(); item.HelpText = request.HelpText;
        item.DesignNote = request.DesignNote; item.FieldType = request.FieldType; item.IsRequired = request.IsRequired; item.IsEnabled = request.IsEnabled; item.DisplayOrder = request.DisplayOrder; item.ValidationJson = request.ValidationJson;
        await db.SaveChangesAsync(ct); return item.Id;
    }

    public async Task PublishAsync(Guid versionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct); var version = await RequireDraft(db, versionId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        version.Status = ValidationTemplateStatuses.Published; version.PublishedAt = DateTime.UtcNow; version.UpdatedAt = DateTime.UtcNow;
        foreach (var field in db.ValidationFieldDefinitions.Where(x => x.SectionDefinition.TemplateVersionId == versionId)) field.WasPublished = true;
        foreach (var option in db.ValidationFieldOptions.Where(x => x.FieldDefinition.SectionDefinition.TemplateVersionId == versionId)) option.WasPublished = true;
        foreach (var item in db.ValidationTestItemDefinitions.Where(x => x.TestItemCategory.TemplateVersionId == versionId)) item.WasPublished = true;
        foreach (var parameter in db.ValidationTestItemParameterDefinitions.Where(x => x.TestItemDefinition.TestItemCategory.TemplateVersionId == versionId)) parameter.WasPublished = true;
        foreach (var option in db.ValidationTestItemParameterOptions.Where(x => x.ParameterDefinition.TestItemDefinition.TestItemCategory.TemplateVersionId == versionId)) option.WasPublished = true;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }

    public async Task<Guid> CloneDraftAsync(Guid versionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var source = await db.ValidationFormTemplateVersions.AsNoTracking().SingleAsync(x => x.Id == versionId, ct);
        if (source.Status != ValidationTemplateStatuses.Published) throw new InvalidOperationException("Only a Published version can be cloned.");
        if (await db.ValidationFormTemplateVersions.AnyAsync(x => x.TemplateId == source.TemplateId && x.Status == ValidationTemplateStatuses.Draft, ct)) throw new InvalidOperationException("A Draft version already exists.");
        var sections = await db.ValidationSectionDefinitions.AsNoTracking().Where(x => x.TemplateVersionId == versionId).Include(x => x.Fields).ThenInclude(x => x.Options).Include(x => x.Fields).ThenInclude(x => x.Conditions).ToListAsync(ct);
        var categories = await db.ValidationTestItemCategories.AsNoTracking().Where(x => x.TemplateVersionId == versionId).Include(x => x.TestItems).ThenInclude(x => x.Parameters).ThenInclude(x => x.Options).ToListAsync(ct);
        var next = new ValidationFormTemplateVersion { Id = Guid.NewGuid(), TemplateId = source.TemplateId, VersionNumber = await db.ValidationFormTemplateVersions.Where(x => x.TemplateId == source.TemplateId).MaxAsync(x => x.VersionNumber, ct) + 1, Status = ValidationTemplateStatuses.Draft, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Add(next); var sectionIds = new Dictionary<Guid, Guid>(); var fieldIds = new Dictionary<Guid, Guid>();
        foreach (var s in sections) { var id = Guid.NewGuid(); sectionIds[s.Id] = id; db.Add(new ValidationSectionDefinition { Id = id, TemplateVersionId = next.Id, Code = s.Code, DisplayLabel = s.DisplayLabel, Description = s.Description, AlwaysVisible = s.AlwaysVisible, IsEnabled = s.IsEnabled, DisplayOrder = s.DisplayOrder }); foreach (var f in s.Fields) fieldIds[f.Id] = Guid.NewGuid(); }
        foreach (var s in sections) foreach (var f in s.Fields) { var id = fieldIds[f.Id]; db.Add(new ValidationFieldDefinition { Id = id, SectionDefinitionId = sectionIds[s.Id], ParentFieldDefinitionId = f.ParentFieldDefinitionId is null ? null : fieldIds[f.ParentFieldDefinitionId.Value], Code = f.Code, DisplayLabel = f.DisplayLabel, HelpText = f.HelpText, DesignNote = f.DesignNote, FieldType = f.FieldType, IsRequired = f.IsRequired, IsEnabled = f.IsEnabled, DisplayOrder = f.DisplayOrder, IsSensitive = f.IsSensitive, DataSource = f.DataSource, ValidationJson = f.ValidationJson }); foreach (var o in f.Options) db.Add(new ValidationFieldOption { Id = Guid.NewGuid(), FieldDefinitionId = id, ValueCode = o.ValueCode, DisplayLabel = o.DisplayLabel, IsEnabled = o.IsEnabled, DisplayOrder = o.DisplayOrder }); foreach (var c in f.Conditions) db.Add(new ValidationFieldCondition { Id = Guid.NewGuid(), TargetFieldDefinitionId = id, SourceFieldCode = c.SourceFieldCode, Operator = c.Operator, ExpectedValue = c.ExpectedValue, Action = c.Action }); }
        var oldSectionMappings = await db.ValidationProductTypeSectionMappings.AsNoTracking().Where(x => sectionIds.Keys.Contains(x.SectionDefinitionId)).ToListAsync(ct); db.AddRange(oldSectionMappings.Select(x => new ValidationProductTypeSectionMapping { ProductTypeId = x.ProductTypeId, SectionDefinitionId = sectionIds[x.SectionDefinitionId] }));
        var categoryIds = new Dictionary<Guid, Guid>(); var itemIds = new Dictionary<Guid, Guid>(); var parameterIds = new Dictionary<Guid, Guid>();
        foreach (var c in categories) { categoryIds[c.Id] = Guid.NewGuid(); db.Add(new ValidationTestItemCategory { Id = categoryIds[c.Id], TemplateVersionId = next.Id, Code = c.Code, DisplayLabel = c.DisplayLabel, AlwaysVisible = c.AlwaysVisible, IsEnabled = c.IsEnabled, DisplayOrder = c.DisplayOrder }); foreach (var i in c.TestItems) { itemIds[i.Id] = Guid.NewGuid(); foreach (var p in i.Parameters) parameterIds[p.Id] = Guid.NewGuid(); } }
        foreach (var c in categories) foreach (var i in c.TestItems) { db.Add(new ValidationTestItemDefinition { Id = itemIds[i.Id], TestItemCategoryId = categoryIds[c.Id], Code = i.Code, DisplayLabel = i.DisplayLabel, Description = i.Description, DesignNote = i.DesignNote, IsMandatory = i.IsMandatory, IsEnabled = i.IsEnabled, IsArchived = i.IsArchived, DisplayOrder = i.DisplayOrder }); foreach (var p in i.Parameters) { db.Add(new ValidationTestItemParameterDefinition { Id = parameterIds[p.Id], TestItemDefinitionId = itemIds[i.Id], ParentParameterDefinitionId = p.ParentParameterDefinitionId is null ? null : parameterIds[p.ParentParameterDefinitionId.Value], Code = p.Code, DisplayLabel = p.DisplayLabel, HelpText = p.HelpText, DesignNote = p.DesignNote, FieldType = p.FieldType, IsRequired = p.IsRequired, IsEnabled = p.IsEnabled, DisplayOrder = p.DisplayOrder, ValidationJson = p.ValidationJson }); foreach (var o in p.Options) db.Add(new ValidationTestItemParameterOption { Id = Guid.NewGuid(), ParameterDefinitionId = parameterIds[p.Id], ValueCode = o.ValueCode, DisplayLabel = o.DisplayLabel, IsEnabled = o.IsEnabled, DisplayOrder = o.DisplayOrder }); } }
        var oldCategoryMappings = await db.ValidationProductTypeTestCategoryMappings.AsNoTracking().Where(x => categoryIds.Keys.Contains(x.TestItemCategoryId)).ToListAsync(ct); db.AddRange(oldCategoryMappings.Select(x => new ValidationProductTypeTestCategoryMapping { ProductTypeId = x.ProductTypeId, TestItemCategoryId = categoryIds[x.TestItemCategoryId] }));
        await db.SaveChangesAsync(ct); return next.Id;
    }

    private static async Task<List<ValidationSectionDto>> LoadSections(AppDbContext db, Guid versionId, CancellationToken ct)
    {
        var data = await db.ValidationSectionDefinitions.AsNoTracking().Where(x => x.TemplateVersionId == versionId && x.IsEnabled)
            .Include(x => x.Fields).ThenInclude(x => x.Options).Include(x => x.Fields).ThenInclude(x => x.Conditions)
            .AsSplitQuery().OrderBy(x => x.DisplayOrder).ToListAsync(ct);
        return data.Select(s => new ValidationSectionDto(s.Id, s.Code, s.DisplayLabel, s.Description, s.AlwaysVisible, s.IsEnabled, s.DisplayOrder,
            s.Fields.Where(x => x.ParentFieldDefinitionId == null).OrderBy(x => x.DisplayOrder).Select(x => Field(x, s.Fields)).ToList())).ToList();
    }
    private static ValidationFieldDto Field(ValidationFieldDefinition f, IReadOnlyList<ValidationFieldDefinition> all) => new(f.Id, f.SectionDefinitionId, f.ParentFieldDefinitionId, f.Code, f.DisplayLabel, f.HelpText, f.DesignNote, f.FieldType, f.IsRequired, f.IsEnabled, f.DisplayOrder, f.IsSensitive, f.DataSource, f.ValidationJson,
        f.Options.OrderBy(x => x.DisplayOrder).Select(x => new ValidationOptionDto(x.Id, x.ValueCode, x.DisplayLabel, x.IsEnabled, x.DisplayOrder)).ToList(),
        f.Conditions.Select(x => new ValidationConditionDto(x.Id, x.SourceFieldCode, x.Operator, x.ExpectedValue, x.Action)).ToList(),
        all.Where(x => x.ParentFieldDefinitionId == f.Id).OrderBy(x => x.DisplayOrder).Select(x => Field(x, all)).ToList());
    private static async Task<List<ValidationTestCategoryDto>> LoadCategories(AppDbContext db, Guid versionId, CancellationToken ct)
    {
        var data = await db.ValidationTestItemCategories.AsNoTracking().Where(x => x.TemplateVersionId == versionId && x.IsEnabled)
            .Include(x => x.TestItems).ThenInclude(x => x.Parameters).ThenInclude(x => x.Options).AsSplitQuery().OrderBy(x => x.DisplayOrder).ToListAsync(ct);
        return data.Select(c => new ValidationTestCategoryDto(c.Id, c.Code, c.DisplayLabel, c.AlwaysVisible, c.IsEnabled, c.DisplayOrder,
            c.TestItems.Where(x => !x.IsArchived).OrderBy(x => x.DisplayOrder).Select(i => new ValidationTestItemDto(i.Id, i.TestItemCategoryId, i.Code, i.DisplayLabel, i.Description, i.DesignNote, i.IsMandatory, i.IsEnabled, i.IsArchived, i.DisplayOrder,
                i.Parameters.Where(x => x.ParentParameterDefinitionId == null).OrderBy(x => x.DisplayOrder).Select(x => Parameter(x, i.Parameters)).ToList())).ToList())).ToList();
    }
    private static ValidationParameterDto Parameter(ValidationTestItemParameterDefinition p, IReadOnlyList<ValidationTestItemParameterDefinition> all) => new(p.Id, p.ParentParameterDefinitionId, p.Code, p.DisplayLabel, p.HelpText, p.DesignNote, p.FieldType, p.IsRequired, p.IsEnabled, p.DisplayOrder, p.ValidationJson,
        p.Options.OrderBy(x => x.DisplayOrder).Select(x => new ValidationOptionDto(x.Id, x.ValueCode, x.DisplayLabel, x.IsEnabled, x.DisplayOrder)).ToList(), all.Where(x => x.ParentParameterDefinitionId == p.Id).OrderBy(x => x.DisplayOrder).Select(x => Parameter(x, all)).ToList());
    private static ValidationTemplateVersionDto Version(ValidationFormTemplateVersion x) => new(x.Id, x.VersionNumber, x.Status, x.PublishedAt);
    private static async Task<ValidationFormTemplateVersion> RequireDraft(AppDbContext db, Guid versionId, CancellationToken ct)
    { var v = await db.ValidationFormTemplateVersions.SingleOrDefaultAsync(x => x.Id == versionId, ct) ?? throw new KeyNotFoundException("Template version not found."); if (v.Status != ValidationTemplateStatuses.Draft) throw new InvalidOperationException("Published versions are immutable; clone a new Draft version."); return v; }
    private static async Task RequireDraftBySection(AppDbContext db, Guid id, CancellationToken ct) => await RequireDraft(db, await db.ValidationSectionDefinitions.Where(x => x.Id == id).Select(x => x.TemplateVersionId).SingleAsync(ct), ct);
    private static async Task RequireDraftByField(AppDbContext db, Guid id, CancellationToken ct) => await RequireDraft(db, await db.ValidationFieldDefinitions.Where(x => x.Id == id).Select(x => x.SectionDefinition.TemplateVersionId).SingleAsync(ct), ct);
    private static async Task RequireDraftByCategory(AppDbContext db, Guid id, CancellationToken ct) => await RequireDraft(db, await db.ValidationTestItemCategories.Where(x => x.Id == id).Select(x => x.TemplateVersionId).SingleAsync(ct), ct);
    private static void Require(string value, string name) { if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} is required."); }
}
