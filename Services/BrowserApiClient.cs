using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace SIT.DepartmentSystem.Web.Services;

public sealed class BrowserApiClient(IJSRuntime js) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private IJSObjectReference? _module;

    public async Task<T> GetAsync<T>(string url)
    {
        var response = await SendAsync("GET", url, null);
        response.EnsureSuccess();
        return JsonSerializer.Deserialize<T>(response.Body, JsonOptions)
            ?? throw new InvalidOperationException("伺服器回傳空白資料。");
    }

    public Task<BrowserApiResponse> PostJsonAsync<T>(string url, T body) =>
        SendAsync("POST", url, body);

    public Task<BrowserApiResponse> PutJsonAsync<T>(string url, T body) =>
        SendAsync("PUT", url, body);

    public Task<BrowserApiResponse> DeleteAsync(string url) =>
        SendAsync("DELETE", url, null);

    public async Task<BrowserApiResponse> UploadFilesAsync(string url, ElementReference inputContainer)
    {
        var module = await GetModuleAsync();
        return await module.InvokeAsync<BrowserApiResponse>("uploadFiles", url, inputContainer);
    }

    private async Task<BrowserApiResponse> SendAsync(string method, string url, object? body)
    {
        var module = await GetModuleAsync();
        return await module.InvokeAsync<BrowserApiResponse>("request", method, url, body);
    }

    private async Task<IJSObjectReference> GetModuleAsync() =>
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/browser-api.js");

    public async ValueTask DisposeAsync()
    {
        if (_module is null) return;

        try
        {
            await _module.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // The interactive server circuit has already disconnected.
        }
        catch (ObjectDisposedException)
        {
            // The JS module has already been disposed.
        }
        finally
        {
            _module = null;
        }
    }
}

public sealed class BrowserApiResponse
{
    [JsonPropertyName("status")]
    public int StatusCode { get; set; }

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    public bool IsSuccessStatusCode => StatusCode is >= 200 and < 300;

    public void EnsureSuccess()
    {
        if (!IsSuccessStatusCode)
            throw new BrowserApiException(StatusCode, Body);
    }
}

public sealed class BrowserApiException(int statusCode, string responseBody)
    : Exception(string.IsNullOrWhiteSpace(responseBody)
        ? $"伺服器處理失敗（{statusCode}）。"
        : responseBody)
{
    public int StatusCode { get; } = statusCode;
    public string ResponseBody { get; } = responseBody;
}
