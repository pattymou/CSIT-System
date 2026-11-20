using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SIT.DepartmentSystem.Web.Components.Shared;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services;

namespace ApprovalFramework.Tests;

public sealed class EquipmentFileDeletionUiTests
{
    private static readonly Guid FileId = Guid.Parse("09ea60fd-bbb6-4d0e-a443-7a75b4b6c063");

    [Theory]
    [InlineData(EquipmentFileDeletionStatuses.Pending, "等待 KM 清理")]
    [InlineData(EquipmentFileDeletionStatuses.Dispatching, "等待 KM 清理")]
    [InlineData(EquipmentFileDeletionStatuses.Accepted, "等待 KM 清理")]
    [InlineData(EquipmentFileDeletionStatuses.Processing, "KM 刪除處理中")]
    [InlineData(EquipmentFileDeletionStatuses.KmVerified, "清理本地檔案")]
    [InlineData(EquipmentFileDeletionStatuses.LocalCleanup, "清理本地檔案")]
    public async Task ExistingDeletion_ResumesProgressWithoutLocalState(string status, string expected)
    {
        var html = await RenderAsync(SampleFile(status));

        Assert.Contains(expected, html);
        Assert.Contains("刪除中…", html);
        Assert.Contains("disabled", html);
        Assert.Contains("重新整理刪除狀態", html);
        Assert.Contains("test-file.pdf", html);
    }

    [Fact]
    public async Task ConfirmedRequest_RendersSendingStateAndDisablesRepeatClick()
    {
        var html = await RenderAsync(SampleFile(), submitting: true);

        Assert.Contains("送出刪除要求中…", html);
        Assert.Contains("刪除中…", html);
        Assert.Contains("disabled", html);
    }

    [Fact]
    public async Task AcceptedResponse_KeepsFileAndShowsWaitingState()
    {
        var html = await RenderAsync(SampleFile(), accepted: true);

        Assert.Contains("刪除要求已送出，正在等待 KM 清理", html);
        Assert.Contains("test-file.pdf", html);
        Assert.Contains("刪除中…", html);
        Assert.Contains("disabled", html);
    }

    [Theory]
    [InlineData(EquipmentFileDeletionStatuses.Failed, "刪除失敗")]
    [InlineData(EquipmentFileDeletionStatuses.CleanupFailed, "本地檔案清理失敗")]
    public async Task Failure_ShowsFriendlyInformationWithoutRawBackendError(string status, string expected)
    {
        var html = await RenderAsync(SampleFile(status));

        Assert.Contains(expected, html);
        Assert.DoesNotContain("private database path", html);
        Assert.Contains("disabled", html);
        Assert.Contains("重新整理刪除狀態", html);
    }

    [Fact]
    public async Task CompletedDeletion_RemovesRow()
    {
        var html = await RenderAsync(SampleFile(EquipmentFileDeletionStatuses.Completed));

        Assert.DoesNotContain("test-file.pdf", html);
        Assert.Contains("目前沒有設備文件或照片", html);
    }

    [Fact]
    public async Task LongRunningDeletion_ShowsReturnLaterGuidance()
    {
        var html = await RenderAsync(SampleFile(EquipmentFileDeletionStatuses.Processing), longRunning: true);

        Assert.Contains("仍在處理，可稍後回來查看", html);
        Assert.Contains("重新整理刪除狀態", html);
    }

    [Fact]
    public void EditPage_RefreshesOnlyFiles_AndStopsPollingOnNavigationAndDispose()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "Components", "Pages", "ApparatusEdit.razor"));
        var refresh = source.Split("private async Task RefreshFileListAsync(CancellationToken cancellationToken)")[1]
            .Split("private async Task RefreshDeletionStatusAsync()")[0];

        Assert.Contains("if (!_deletionSubmitting.Add(fileId)", source);
        Assert.Contains("response.StatusCode is 202 or 204", source);
        Assert.Contains("response.StatusCode == 204 && _files.Any", source);
        Assert.Contains("new PeriodicTimer(TimeSpan.FromSeconds(DeletionPollSeconds))", source);
        Assert.Contains("Nav.LocationChanged += OnLocationChanged", source);
        Assert.Contains("Nav.LocationChanged -= OnLocationChanged", source);
        Assert.Contains("StopDeletionPolling()", source);
        Assert.Contains("/files", refresh);
        Assert.DoesNotContain("_form =", refresh);
        Assert.DoesNotContain("InitializeAsync", refresh);
        Assert.DoesNotContain("SaveAsync", refresh);
    }

    private static ApparatusFileDto SampleFile(string? status = null) => new()
    {
        Id = FileId, FileName = "test-file.pdf", FileSize = 100,
        DownloadUrl = "/api/assets/files/test/content", DeletionStatus = status,
        DeletionError = "private database path", CanDelete = true
    };

    private static async Task<string> RenderAsync(ApparatusFileDto file, bool submitting = false,
        bool accepted = false, bool longRunning = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IJSRuntime, UnusedJsRuntime>();
        services.AddSingleton<BrowserApiClient>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(EquipmentFileTable.Files)] = new List<ApparatusFileDto> { file },
            [nameof(EquipmentFileTable.OnDelete)] = EventCallback.Factory.Create<Guid>(
                new object(), (Action<Guid>)(_ => { })),
            [nameof(EquipmentFileTable.OnRefreshDeletionStatus)] = EventCallback.Factory.Create(
                new object(), (Action)(() => { })),
            [nameof(EquipmentFileTable.SubmittingFileIds)] = submitting ? new HashSet<Guid> { FileId } : new HashSet<Guid>(),
            [nameof(EquipmentFileTable.AcceptedFileIds)] = accepted ? new HashSet<Guid> { FileId } : new HashSet<Guid>(),
            [nameof(EquipmentFileTable.LongRunningFileIds)] = longRunning ? new HashSet<Guid> { FileId } : new HashSet<Guid>()
        });
        return await renderer.Dispatcher.InvokeAsync(async () =>
            System.Net.WebUtility.HtmlDecode((await renderer.RenderComponentAsync<EquipmentFileTable>(parameters))
                .ToHtmlString()));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SIT.DepartmentSystem.Web.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException();
    }

    private sealed class UnusedJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new NotSupportedException();
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier,
            CancellationToken cancellationToken, object?[]? args) => throw new NotSupportedException();
    }
}
