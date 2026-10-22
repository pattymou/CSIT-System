using System.Text.Json;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;

namespace ApprovalFramework.Tests;

public sealed class DynamicValidationApplicationTests
{
    [Fact]
    public void DynamicPayload_IsStoredOnTheExistingVerificationApplication()
    {
        var now = DateTime.UtcNow;
        var entity = VerificationApplication.CreateDraft(Guid.NewGuid(), "VA-TEST", Guid.NewGuid(), Guid.NewGuid(),
            "applicant", "Applicant", "a@example.test", "RD", null,
            new VerificationApplicationContent { ProjectName = "Project", ProductModel = "RTF7907VW-D223" }, now);
        var versionId = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new ValidationRequestSubmissionDto
        {
            TemplateVersionId = versionId, TemplateVersion = 3, ProductTypeCode = "WIFI_AP",
            FieldValues = new(StringComparer.OrdinalIgnoreCase) { ["MODEL_NAME"] = ["RTF7907VW-D223"] }
        });

        entity.SetValidationRequest(versionId, 3, "WIFI_AP", json, now.AddMinutes(1));

        Assert.Equal(versionId, entity.ValidationTemplateVersionId);
        Assert.Equal(3, entity.ValidationTemplateVersionNumber);
        Assert.Equal("WIFI_AP", entity.ValidationProductTypeCode);
        Assert.Contains("RTF7907VW-D223", entity.ValidationFormPayloadJson);
        Assert.Equal("VA-TEST", entity.ApplicationNo);
    }

    [Fact]
    public void LegacyApplication_RemainsValidWithoutDynamicPayload()
    {
        var entity = VerificationApplication.CreateDraft(Guid.NewGuid(), "VA-LEGACY", Guid.NewGuid(), Guid.NewGuid(),
            "applicant", "Applicant", "a@example.test", "RD", null,
            new VerificationApplicationContent { ProjectName = "Legacy" }, DateTime.UtcNow);

        Assert.Null(entity.ValidationFormPayloadJson);
        Assert.Equal(VerificationApplicationStatus.Draft, entity.Status);
    }

    [Fact]
    public void SubmissionPayload_RoundTripsSnapshotAndAnswers()
    {
        var versionId = Guid.NewGuid();
        var snapshot = new ValidationRequestPreviewDto("VALIDATION_TEST_REQUEST", versionId, 2, "WIFI_AP", "Wi-Fi AP", [], []);
        var source = new ValidationRequestSubmissionDto
        {
            TemplateVersionId = versionId, TemplateVersion = 2, ProductTypeCode = "WIFI_AP", FormSnapshot = snapshot,
            FieldValues = new(StringComparer.OrdinalIgnoreCase) { ["MODEL_NAME"] = ["RTF7907VW-D223"], ["BANDS"] = ["2G", "5G"] },
            SelectedTestItemCodes = new(StringComparer.OrdinalIgnoreCase) { "THROUGHPUT" }
        };

        var restored = JsonSerializer.Deserialize<ValidationRequestSubmissionDto>(JsonSerializer.Serialize(source))!;

        Assert.Equal("RTF7907VW-D223", restored.FieldValues["MODEL_NAME"][0]);
        Assert.Equal(2, restored.FormSnapshot!.TemplateVersion);
        Assert.Contains("THROUGHPUT", restored.SelectedTestItemCodes);
    }
}
