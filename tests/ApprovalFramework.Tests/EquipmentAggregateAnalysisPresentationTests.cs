using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Controllers;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace ApprovalFramework.Tests;

public sealed class EquipmentAggregateAnalysisPresentationTests
{
    [Fact]
    public async Task ExistingEquipmentWithoutFiles_ReturnsViewAndRendersBlank()
    {
        await using var db = NewDb();
        db.Apparatuses.Add(new Apparatus
        {
            Id = "EQ-EMPTY", ModuleCode = "equipment", Name = "Empty",
            Kind = "Test", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var controller = NewController(db);

        var response = await controller.Get(" EQ-EMPTY ", "EQUIPMENT", default);

        var view = Assert.IsType<EquipmentAggregateAnalysisView>(Assert.IsType<OkObjectResult>(response).Value);
        Assert.Equal("NoEligibleDocuments", view.Status);
        Assert.True(EquipmentAggregateAnalysisPresentation.IsBlank(view, false));
        Assert.False(EquipmentAggregateAnalysisPresentation.IsBlank(view, true));
        Assert.Empty(db.EquipmentAggregateAnalysisRequests);
    }

    [Fact]
    public void BlankPresentation_PreservesPendingCompletedFailedAndStale()
    {
        var empty = new EquipmentAggregateAnalysisView("EQ", "equipment", true, "hash", 0, 0,
            "NoEligibleDocuments", false, null, null, null, null);
        Assert.False(EquipmentAggregateAnalysisPresentation.IsBlank(empty with
        {
            Status = "Pending", PendingDocumentCount = 1
        }, false));
        Assert.False(EquipmentAggregateAnalysisPresentation.IsBlank(empty with { Status = "Failed" }, false));
        Assert.False(EquipmentAggregateAnalysisPresentation.IsBlank(empty with
        {
            Status = "Stale", IsStale = true, SnapshotId = Guid.NewGuid()
        }, false));
        Assert.False(EquipmentAggregateAnalysisPresentation.IsBlank(empty with
        {
            Status = "Completed", Result = new EquipmentAggregateAnalysisResult(
                "Analysis", [], [], [], [], [], [])
        }, false));
    }

    [Fact]
    public async Task MissingEquipment_RemainsRealNotFound()
    {
        await using var db = NewDb();
        var response = await NewController(db).Get("EQ-MISSING", "equipment", default);
        Assert.IsType<NotFoundResult>(response);
    }

    [Fact]
    public void ProblemDetailsTraceId_OnlyAcceptsStructuredTraceIdentifier()
    {
        const string trace = "00-dfb97e695d4cfffa418c94d703e304a0-2070cca277fb3f7d-00";
        Assert.Equal(trace, EquipmentAggregateAnalysisPresentation.ProblemTraceId(
            "{\"status\":404,\"traceId\":\"" + trace + "\",\"detail\":\"private data\"}"));
        Assert.Null(EquipmentAggregateAnalysisPresentation.ProblemTraceId(
            "{\"traceId\":\"secret\\nvalue\"}"));
        Assert.Null(EquipmentAggregateAnalysisPresentation.ProblemTraceId("not json"));
    }

    private static AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static EquipmentAggregateAnalysisController NewController(AppDbContext db) => new(
        new EquipmentAggregateAnalysisService(db,
            Options.Create(new EquipmentAggregateAnalysisOptions { Enabled = true }), TimeProvider.System),
        db, NullLogger<EquipmentAggregateAnalysisController>.Instance)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
    };
}
