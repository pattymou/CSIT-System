using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services.ValidationRequests;

namespace ApprovalFramework.Tests;

public sealed class ValidationRequestPhase1Tests
{
    [Fact]
    public void DomainHasNoProjectCaseOrTaskRelationship()
    {
        var types = typeof(ValidationFormTemplate).Assembly.GetTypes().Where(x => x.Namespace == typeof(ValidationFormTemplate).Namespace && x.Name.StartsWith("Validation", StringComparison.Ordinal));
        var forbidden = new[] { "Project", "Case", "Task", "ModuleRecord", "ModuleRecordCase", "ModuleRecordTask" };
        foreach (var property in types.SelectMany(x => x.GetProperties()))
            Assert.DoesNotContain(forbidden, name => property.PropertyType.Name == name || property.Name.EndsWith(name + "Id", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(AppDbContext).GetProperties(), x => x.Name.Contains("ValidationRequestSubmission", StringComparison.Ordinal));
    }

    [Fact]
    public void SupportsRequiredDynamicFieldAndConditionTypes()
    {
        Assert.Equal(new[] { "Text", "TextArea", "SensitiveText", "Number", "Dropdown", "SingleSelect", "Checkbox", "MultiSelect", "Attachment", "Group", "RepeatableText", "RepeatableGroup", "Lookup" }, ValidationFieldTypes.All);
        Assert.Equal(new[] { "Equals", "NotEquals", "Contains", "NotContains", "IsTrue", "IsFalse" }, ValidationConditionOperators.All);
        Assert.Equal(new[] { "Visible", "Required" }, ValidationConditionActions.All);
    }

    [Theory]
    [InlineData("WIFI_AP", "COMMON_PRODUCT_INFO,TYPE_WIFI", "A,B")]
    [InlineData("FIVE_G_FWA", "COMMON_PRODUCT_INFO,TYPE_WIFI,TYPE_CELLULAR_UE", "A,B,C")]
    [InlineData("FIVE_G_DONGLE_NO_WIFI", "COMMON_PRODUCT_INFO,TYPE_CELLULAR_UE", "A,C")]
    [InlineData("SMALLCELL", "COMMON_PRODUCT_INFO,TYPE_SMALLCELL", "A,D")]
    public async Task PreviewUsesDatabaseMappings(string product, string sections, string categories)
    {
        var service = await CreateService();
        var preview = await service.GetPreviewAsync(product);
        Assert.Equal(sections.Split(','), preview.Sections.Select(x => x.Code));
        Assert.Equal(categories.Split(','), preview.TestCategories.Select(x => x.Code));
    }

    [Fact]
    public async Task CanonicalRequiredOptionalAndSensitiveRulesAreSeeded()
    {
        var service = await CreateService();
        var cellular = await service.GetPreviewAsync("FIVE_G_ODU");
        var fields = Flatten(cellular.Sections).ToDictionary(x => x.Code);
        Assert.False(fields["THREE_GPP_RELEASE"].IsRequired);
        Assert.True(fields["SUPPORTED_RATS"].IsRequired);
        Assert.True(fields["NR_BANDS"].IsRequired);
        Assert.True(fields["LTE_BANDS"].IsRequired);
        Assert.False(fields["CA_DC_COMBINATIONS"].IsRequired);
        Assert.False(fields["FR2_SUPPORTED"].IsRequired);
        Assert.False(fields["APN_DATA"].IsRequired);
        Assert.True(fields["AT_COMMAND"].IsRequired);
        Assert.False(fields["ESIM_SUPPORT"].IsRequired);
        Assert.True(fields["DEFAULT_PASSWORD"].IsSensitive);
        Assert.True(fields["SSH_PASSWORD"].IsSensitive);
        Assert.Contains(fields["FR2_BANDS"].Conditions, x => x.SourceFieldCode == "FR2_SUPPORTED" && x.Operator == "IsTrue" && x.Action == "Visible");

        var small = await service.GetPreviewAsync("SMALLCELL");
        var smallFields = Flatten(small.Sections).ToDictionary(x => x.Code);
        Assert.True(smallFields["SMALLCELL_CHIPSET"].IsRequired);
        Assert.True(smallFields["SMALLCELL_MODEM_DRIVER"].IsRequired);
        Assert.True(smallFields["RF_POWER_ANTENNA"].IsRequired);
        Assert.All(new[] { "DUPLEX", "CELL_IDENTITY", "RF_ANTENNA_CONFIG", "GPS_ANTENNA", "POWER_SUPPLY", "POWER_CONSUMPTION", "SYNC_SOURCE", "MANAGEMENT_PORT", "LED_DISPLAY", "BACKHAUL", "MAXIMUM_UE", "DEPLOYMENT" }, code => Assert.False(smallFields[code].IsRequired));
    }

    [Fact]
    public async Task CategoryAHasNoInventedItemsAndSmallCellParametersMatchContract()
    {
        var service = await CreateService(); var preview = await service.GetPreviewAsync("SMALLCELL");
        Assert.Empty(preview.TestCategories.Single(x => x.Code == "A").TestItems);
        var category = preview.TestCategories.Single(x => x.Code == "D");
        Assert.Equal(10, category.TestItems.Count); Assert.All(category.TestItems, x => Assert.False(x.IsMandatory));
        Assert.Empty(category.TestItems.Single(x => x.Code == "SC_AMP_INTEROPERABILITY").Parameters);
        Assert.Empty(category.TestItems.Single(x => x.Code == "SC_FUNCTIONAL").Parameters);
        Assert.Equal(3, category.TestItems.Single(x => x.Code == "SC_OTA_THROUGHPUT").Parameters.Count);
        Assert.Contains(category.TestItems.SelectMany(x => x.Parameters), x => x.DesignNote!.Contains("no limit is inferred"));
    }

    [Fact]
    public async Task DraftSupportsCrudOrderingMappingsOptionsConditionsAndDelete()
    {
        var factory = await CreateFactory(); var service = new ValidationRequestConfigurationService(factory); var config = await service.GetConfigurationAsync();
        var productId = await service.SaveProductTypeAsync(null, new("TEST_PRODUCT", "Test Product", true, 5));
        var section = config.Sections.Single(x => x.Code == "TYPE_WIFI"); var category = config.TestCategories.Single(x => x.Code == "B");
        await service.SetSectionMappingsAsync(productId, [section.Id]); await service.SetCategoryMappingsAsync(productId, [category.Id]);
        var fieldId = await service.SaveFieldAsync(null, new(section.Id, null, "TEST_DROPDOWN", "Test Dropdown", null, null, "Dropdown", false, true, 5, false, null, null));
        var optionId = await service.SaveOptionAsync(fieldId, null, new("ONE", "One", true, 10));
        var conditionId = await service.SaveConditionAsync(fieldId, null, new("WIFI_ROLE", "Equals", "AP", "Visible"));
        var itemId = await service.SaveTestItemAsync(null, new(category.Id, "TEST_ITEM", "Test Item", "description", null, true, true, 5));
        await service.SaveParameterAsync(itemId, null, new(null, "COUNT", "Count", null, null, "Number", true, true, 10, "{\"integer\":true}"));
        var updated = await service.GetConfigurationAsync(); var product = updated.ProductTypes.Single(x => x.Id == productId);
        Assert.Equal([section.Id], product.SectionIds); Assert.Equal([category.Id], product.TestCategoryIds);
        var field = Flatten(updated.Sections).Single(x => x.Id == fieldId); Assert.Single(field.Options); Assert.Single(field.Conditions);
        var item = updated.TestCategories.SelectMany(x => x.TestItems).Single(x => x.Id == itemId); Assert.True(item.IsMandatory); Assert.Single(item.Parameters);
        await service.DeleteConditionAsync(conditionId); await service.DeleteOptionAsync(optionId); await service.DeleteFieldAsync(fieldId); await service.DeleteTestItemAsync(itemId);
        Assert.DoesNotContain(Flatten((await service.GetConfigurationAsync()).Sections), x => x.Id == fieldId);
    }

    [Fact]
    public async Task PublishedVersionIsImmutableAndCanBeClonedToNewDraft()
    {
        var factory = await CreateFactory(); var service = new ValidationRequestConfigurationService(factory); var config = await service.GetConfigurationAsync();
        await service.PublishAsync(config.Version.Id);
        var field = Flatten(config.Sections).First();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveFieldAsync(field.Id, new(field.SectionId, field.ParentFieldId, field.Code, field.DisplayLabel, field.HelpText, field.DesignNote, field.FieldType, field.IsRequired, field.IsEnabled, field.DisplayOrder, field.IsSensitive, field.DataSource, field.ValidationJson)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteTestItemAsync(config.TestCategories.SelectMany(x => x.TestItems).First().Id));
        var newId = await service.CloneDraftAsync(config.Version.Id); var clone = await service.GetConfigurationAsync();
        Assert.Equal(newId, clone.Version.Id); Assert.Equal(2, clone.Version.VersionNumber); Assert.Equal("Draft", clone.Version.Status);
        Assert.Equal(config.Sections.Count, clone.Sections.Count); Assert.Equal(config.TestCategories.Count, clone.TestCategories.Count);
    }

    [Fact]
    public void MigrationAddsNavigationBeforeExistingMenusWithoutChangingThem()
    {
        var root = FindRepositoryRoot(); var migration = File.ReadAllText(Path.Combine(root, "Migrations", "20261012033700_AddValidationTestRequestPhase1.cs"));
        Assert.Contains("'驗證測試申請單', '/validation-requests'", migration);
        Assert.Contains("'驗證申請單設定', '/validation-request-settings'", migration);
        Assert.Contains("MIN(i.sort_order) - 10", migration);
        Assert.DoesNotContain("UPDATE menu_items", migration, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<ValidationFieldDto> Flatten(IEnumerable<ValidationSectionDto> sections) => Flatten(sections.SelectMany(x => x.Fields));
    private static IEnumerable<ValidationFieldDto> Flatten(IEnumerable<ValidationFieldDto> fields) { foreach (var field in fields) { yield return field; foreach (var child in Flatten(field.Children)) yield return child; } }
    private static async Task<ValidationRequestConfigurationService> CreateService() => new(await CreateFactory());
    private static async Task<TestFactory> CreateFactory()
    {
        var factory = new TestFactory(); await using var db = await factory.CreateDbContextAsync(); await db.Database.EnsureCreatedAsync(); return factory;
    }
    private sealed class TestFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).ConfigureWarnings(x => x.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options;
        public AppDbContext CreateDbContext() => new(_options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
    private static string FindRepositoryRoot() { var path = AppContext.BaseDirectory; while (!File.Exists(Path.Combine(path, "SIT.DepartmentSystem.Web.csproj"))) path = Directory.GetParent(path)?.FullName ?? throw new DirectoryNotFoundException(); return path; }
}
