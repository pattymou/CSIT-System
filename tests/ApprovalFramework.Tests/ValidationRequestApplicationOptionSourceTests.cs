using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Services.ValidationRequests;

namespace ApprovalFramework.Tests;

public sealed class ValidationRequestApplicationOptionSourceTests
{
    [Fact]
    public async Task GetAsync_UsesPublishedValidationCustomerAndPuSources()
    {
        await using var db = CreateDb();
        var template = new ValidationFormTemplate
        {
            Id = Guid.NewGuid(),
            Code = "VALIDATION_TEST_REQUEST",
            DisplayName = "Validation Test Request"
        };
        var published = new ValidationFormTemplateVersion
        {
            Id = Guid.NewGuid(), Template = template, VersionNumber = 1,
            Status = ValidationTemplateStatuses.Published
        };
        var draft = new ValidationFormTemplateVersion
        {
            Id = Guid.NewGuid(), Template = template, VersionNumber = 2,
            Status = ValidationTemplateStatuses.Draft
        };
        var section = new ValidationSectionDefinition
        {
            Id = Guid.NewGuid(), TemplateVersion = published, Code = "COMMON_PRODUCT_INFO",
            DisplayLabel = "A. Product Project Information"
        };
        var customer = new ValidationFieldDefinition
        {
            Id = Guid.NewGuid(), SectionDefinition = section, Code = "CUSTOMER",
            DisplayLabel = "Customer Name / Code", FieldType = "Lookup",
            DataSource = "SystemOption:Customer"
        };
        var puCode = new ValidationFieldDefinition
        {
            Id = Guid.NewGuid(), SectionDefinition = section, Code = "PU_CODE",
            DisplayLabel = "PU Code", FieldType = "Dropdown"
        };
        puCode.Options.Add(new ValidationFieldOption
        {
            Id = Guid.NewGuid(), FieldDefinition = puCode, ValueCode = "2A",
            DisplayLabel = "2A", DisplayOrder = 20
        });
        puCode.Options.Add(new ValidationFieldOption
        {
            Id = Guid.NewGuid(), FieldDefinition = puCode, ValueCode = "1A",
            DisplayLabel = "1A", DisplayOrder = 10
        });
        puCode.Options.Add(new ValidationFieldOption
        {
            Id = Guid.NewGuid(), FieldDefinition = puCode, ValueCode = "DISABLED",
            DisplayLabel = "Disabled", DisplayOrder = 5, IsEnabled = false
        });
        db.AddRange(template, published, draft, section, customer, puCode);
        db.SystemOptions.AddRange(
            new SystemOption { Id = Guid.NewGuid(), Category = SystemOptionCategories.Customer, Name = "Beta", Value = "B", Sort = 20 },
            new SystemOption { Id = Guid.NewGuid(), Category = SystemOptionCategories.Customer, Name = "Alpha", Value = "A", Sort = 10 },
            new SystemOption { Id = Guid.NewGuid(), Category = SystemOptionCategories.Customer, Name = "Hidden", Value = "H", Sort = 1, IsEnabled = false },
            new SystemOption { Id = Guid.NewGuid(), Category = SystemOptionCategories.SubPu, Name = "Legacy", Value = "LEGACY", Sort = 1 });
        await db.SaveChangesAsync();

        var result = await new ValidationRequestApplicationOptionSource(db).GetAsync();

        Assert.Equal(["A", "B"], result.Customers.Select(x => x.ValueCode));
        Assert.Equal(["Alpha", "Beta"], result.Customers.Select(x => x.DisplayLabel));
        Assert.Equal(["1A", "2A"], result.PuCodes.Select(x => x.ValueCode));
        Assert.DoesNotContain(result.PuCodes, x => x.ValueCode == "LEGACY");
    }

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }
}
