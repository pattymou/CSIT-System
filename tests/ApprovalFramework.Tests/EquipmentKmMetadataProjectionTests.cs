using System.Text.Json;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace ApprovalFramework.Tests;

public sealed class EquipmentKmMetadataProjectionTests
{
    [Fact]
    public void Project_UsesActualV2FieldsWithoutInventingEquipmentSpecifications()
    {
        using var payload = JsonDocument.Parse("""
        {
          "document": {
            "title": null,
            "summary": "Quotation summary returned by KM.",
            "documentType": "Other",
            "language": "zh",
            "documentDate": null,
            "keywords": ["DT-01", "robot"]
          },
          "technical": {
            "productNames": [], "modelNumbers": [], "vendorNames": [], "technologies": [],
            "standards": [], "softwareVersions": [], "hardwareVersions": [], "projectNames": [],
            "testItems": [], "issueKeys": [], "errorCodes": [], "measurements": []
          },
          "extraction": {
            "sourceFormat": "xlsx", "method": "Text", "ocrUsed": false,
            "pageCount": null, "sheetNames": ["Sheet1"], "extractedCharacterCount": 2087
          },
          "sourceSpecific": {
            "limitations": [], "specifications": [], "capabilitySummary": null
          },
          "warnings": []
        }
        """);

        var result = EquipmentKmMetadataProjection.Project(payload.RootElement);

        Assert.Equal("Quotation summary returned by KM.", result.Document.Summary);
        Assert.Equal(["DT-01", "robot"], result.Document.Keywords);
        Assert.Equal("xlsx", result.Extraction.SourceFormat);
        Assert.Equal("Text", result.Extraction.Method);
        Assert.False(result.Extraction.OcrUsed);
        Assert.Equal(["Sheet1"], result.Extraction.SheetNames);
        Assert.Equal(2087, result.Extraction.ExtractedCharacterCount);
        Assert.Empty(result.Technical);
        Assert.Empty(result.Specifications);
        Assert.Null(result.CapabilitySummary);
        Assert.Empty(result.Limitations);
    }

    [Fact]
    public void Project_RendersOnlyTechnicalCapabilityLimitationAndEvidenceValuesThatExist()
    {
        using var payload = JsonDocument.Parse("""
        {
          "document": { "title": "Manual", "keywords": [] },
          "technical": {
            "modelNumbers": ["DT-01"],
            "measurements": [{ "metric": "speed", "value": "1.2", "unit": "m/s", "context": "maximum" }]
          },
          "extraction": {},
          "sourceSpecific": {
            "capabilitySummary": "Autonomous navigation",
            "limitations": ["Indoor use"],
            "specifications": [{
              "field": "Battery", "value": "30 Ah",
              "evidence": [{ "page": 2, "excerpt": "Battery capacity" }]
            }]
          },
          "warnings": ["Review manually"]
        }
        """);

        var result = EquipmentKmMetadataProjection.Project(payload.RootElement);

        Assert.Contains(result.Technical, x => x.Label == "型號" && x.Values.SequenceEqual(["DT-01"]));
        Assert.Contains(result.Technical, x => x.Label == "量測資訊" && x.Values.Single() == "speed：1.2 m/s（maximum）");
        Assert.Equal("Autonomous navigation", result.CapabilitySummary);
        Assert.Equal(["Indoor use"], result.Limitations);
        Assert.Equal("Battery", Assert.Single(result.Specifications).Field);
        Assert.Equal("頁碼：2；證據：Battery capacity", result.Specifications[0].Evidence.Single());
        Assert.Equal(["Review manually"], result.Warnings);
    }

    [Theory]
    [InlineData("設備報價單.XLSX", "設備報價單~9f2d1e1f.xlsx")]
    [InlineData("CON.pdf", "_CON~9f2d1e1f.pdf")]
    [InlineData("report: final?.PDF", "report_ final_~9f2d1e1f.pdf")]
    public void FormalManagedAssetName_IsReadableStableAndWindowsSafe(string original, string expected)
    {
        var id = Guid.Parse("9f2d1e1f-cbc9-4423-b592-3dd949cdbf21");

        var result = ManagedAssetFileStorage.StoredFileName(id, original);

        Assert.Equal(expected, result);
        Assert.Equal(result, ManagedAssetFileStorage.StoredFileName(id, original));
        Assert.True(result.Length <= FormalStoredFileNameRules.MaximumFileNameLength);
    }
}
