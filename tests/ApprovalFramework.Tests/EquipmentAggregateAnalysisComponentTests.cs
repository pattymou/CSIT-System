using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SIT.DepartmentSystem.Web.Components.Shared;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace ApprovalFramework.Tests;

public sealed class EquipmentAggregateAnalysisComponentTests
{
    private const string Trace = "00-dfb97e695d4cfffa418c94d703e304a0-2070cca277fb3f7d-00";

    [Fact]
    public async Task NoFilesAndNoAnalysis_RendersOnlyHeading()
    {
        var view = EmptyView();
        var (html, _) = await RenderAsync(new BrowserApiResponse
        {
            StatusCode = 200, Body = JsonSerializer.Serialize(view, JsonSerializerOptions.Web)
        }, false);

        Assert.Contains("設備綜合分析", html);
        Assert.DoesNotContain("Manifest", html);
        Assert.DoesNotContain("無有效文件", html);
        Assert.DoesNotContain("尚無設備綜合分析", html);
        Assert.DoesNotContain("讀取設備綜合分析中", html);
    }

    [Fact]
    public async Task FilesWaitingForKm_KeepPendingPresentation()
    {
        var view = EmptyView() with { Status = "Pending", PendingDocumentCount = 1 };
        var (html, _) = await RenderAsync(new BrowserApiResponse
        {
            StatusCode = 200, Body = JsonSerializer.Serialize(view, JsonSerializerOptions.Web)
        }, true);

        Assert.Contains("等待 KM", html);
        Assert.Contains("仍在等待 KM 單檔分析", html);
    }

    [Fact]
    public async Task CompletedKmAnalysis_StillRendersResult()
    {
        var view = EmptyView() with
        {
            Status = "Completed", EligibleDocumentCount = 1,
            Result = new EquipmentAggregateAnalysisResult("Verified overview", [], [], [], [], [], [])
        };
        var (html, _) = await RenderAsync(new BrowserApiResponse
        {
            StatusCode = 200, Body = JsonSerializer.Serialize(view, JsonSerializerOptions.Web)
        }, true);

        Assert.Contains("分析完成", html);
        Assert.Contains("Verified overview", html);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(500)]
    public async Task ApiFailure_ShowsFriendlyMessageAndLogsOnlyStatusAndTrace(int status)
    {
        var body = "{\"status\":" + status + ",\"traceId\":\"" + Trace +
            "\",\"detail\":\"private diagnostic JSON\"}";
        var (html, logs) = await RenderAsync(new BrowserApiResponse
        {
            StatusCode = status, Body = body
        }, false);

        Assert.Contains("設備綜合分析暫時無法取得", html);
        Assert.DoesNotContain("private diagnostic JSON", html);
        Assert.DoesNotContain(Trace, html);
        Assert.DoesNotContain("\"status\"", html);
        Assert.Contains(logs, line => line.Contains($"StatusCode={status}") && line.Contains(Trace));
        Assert.DoesNotContain(logs, line => line.Contains("private diagnostic JSON"));
    }

    private static EquipmentAggregateAnalysisView EmptyView() => new(
        "EQ-EMPTY", "equipment", true, "manifest", 0, 0, "NoEligibleDocuments", false,
        null, null, null, null);

    private static async Task<(string Html, List<string> Logs)> RenderAsync(
        BrowserApiResponse response, bool hasFiles)
    {
        var logs = new List<string>();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(new CaptureLoggerProvider(logs)));
        services.AddSingleton<IJSRuntime>(new FakeJsRuntime(response));
        services.AddSingleton<BrowserApiClient>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<EquipmentMetadataSummary>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    [nameof(EquipmentMetadataSummary.ApparatusId)] = "EQ-EMPTY",
                    [nameof(EquipmentMetadataSummary.HasFiles)] = hasFiles
                }))).ToHtmlString());
        return (System.Net.WebUtility.HtmlDecode(html), logs);
    }

    private sealed class FakeJsRuntime(BrowserApiResponse response) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            new((TValue)(object)new FakeModule(response));

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier,
            CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }

    private sealed class FakeModule(BrowserApiResponse response) : IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            new((TValue)(object)response);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier,
            CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CaptureLoggerProvider(List<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(lines);
        public void Dispose() { }
    }

    private sealed class CaptureLogger(List<string> lines) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoopScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Add(formatter(state, exception));
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();
        public void Dispose() { }
    }
}
