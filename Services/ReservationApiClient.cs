using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Services;

public sealed class ReservationApiClient(IJSRuntime js) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private IJSObjectReference? _module;

    public Task<ReservationDetailDto> CreateAsync(CreateReservationRequest request) =>
        SendAsync<ReservationDetailDto>("POST", "/api/reservations", request);

    public Task<ReservationDetailDto> UpdateAsync(Guid id, UpdateReservationRequest request) =>
        SendAsync<ReservationDetailDto>("PUT", $"/api/reservations/{id}", request);

    public Task<List<ReservationListDto>> ListAsync(ReservationStatus? status = null, bool active = false)
    {
        var query = active
            ? "?active=true"
            : status.HasValue ? $"?status={status.Value}" : string.Empty;
        return SendAsync<List<ReservationListDto>>("GET", "/api/reservations" + query, null);
    }

    public Task<List<ReservationListDto>> StaffListAsync() =>
        SendAsync<List<ReservationListDto>>("GET", "/api/reservations/review", null);

    public async Task<List<ReservationOverviewDto>> OverviewAsync(ReservationOverviewQuery query)
    {
        query.Page = 1;
        query.PageSize = 200;
        return (await OverviewPageAsync(query)).Items;
    }

    public Task<ReservationOverviewPageDto> OverviewPageAsync(ReservationOverviewQuery query)
    {
        var values = new List<string>
        {
            $"from={Uri.EscapeDataString(query.From.ToUniversalTime().ToString("O"))}",
            $"to={Uri.EscapeDataString(query.To.ToUniversalTime().ToString("O"))}",
            $"includeHistory={query.IncludeHistory.ToString().ToLowerInvariant()}",
            $"page={query.Page}",
            $"pageSize={query.PageSize}"
        };
        if (!string.IsNullOrWhiteSpace(query.ApparatusId)) values.Add($"apparatusId={Uri.EscapeDataString(query.ApparatusId)}");
        if (!string.IsNullOrWhiteSpace(query.Department)) values.Add($"department={Uri.EscapeDataString(query.Department)}");
        if (query.Status.HasValue) values.Add($"status={query.Status.Value}");
        if (!string.IsNullOrWhiteSpace(query.Borrower)) values.Add($"borrower={Uri.EscapeDataString(query.Borrower)}");
        return SendAsync<ReservationOverviewPageDto>("GET", "/api/reservations/overview?" + string.Join("&", values), null);
    }

    public Task<ReservationPolicySettings> GetPolicySettingsAsync() =>
        SendAsync<ReservationPolicySettings>("GET", "/api/reservations/policy-settings", null);

    public Task<List<ReservationExtensionRequestDto>> PendingExtensionsAsync() =>
        SendAsync<List<ReservationExtensionRequestDto>>("GET", "/api/reservations/extensions/pending", null);

    public Task<ReservationOverdueResponseDto> OverdueAsync() =>
        SendAsync<ReservationOverdueResponseDto>("GET", "/api/reservations/overdue", null);

    public Task<ReservationExtensionRequestDto> RequestExtensionAsync(Guid id, DateTime requestedEndTime) =>
        SendAsync<ReservationExtensionRequestDto>("POST", $"/api/reservations/{id}/extensions", new ReservationExtensionCreateRequest { RequestedEndTime = requestedEndTime });

    public Task<ReservationExtensionRequestDto> ApproveExtensionAsync(Guid id) =>
        SendAsync<ReservationExtensionRequestDto>("POST", $"/api/reservations/extensions/{id}/approve", null);

    public Task<ReservationExtensionRequestDto> RejectExtensionAsync(Guid id, string reason) =>
        SendAsync<ReservationExtensionRequestDto>("POST", $"/api/reservations/extensions/{id}/reject", new ReservationExtensionReviewRequest { Reason = reason });

    public Task<ReservationExtensionRequestDto> CancelExtensionAsync(Guid id) =>
        SendAsync<ReservationExtensionRequestDto>("POST", $"/api/reservations/extensions/{id}/cancel", null);

    public Task<ReservationDetailDto> GetAsync(Guid id) =>
        SendAsync<ReservationDetailDto>("GET", $"/api/reservations/{id}", null);

    public Task<List<ApparatusListItemDto>> GetBookableApparatusAsync(string? keyword = null) =>
        SendAsync<List<ApparatusListItemDto>>(
            "GET",
            string.IsNullOrWhiteSpace(keyword)
                ? "/api/reservations/apparatus"
                : $"/api/reservations/apparatus?keyword={Uri.EscapeDataString(keyword.Trim())}",
            null);

    public Task<List<ReservationEnvironmentOptionDto>> GetEnvironmentOptionsAsync() =>
        SendAsync<List<ReservationEnvironmentOptionDto>>("GET", "/api/reservations/environment-options", null);

    public Task<ReservationApplicationOptionsDto> GetApplicationOptionsAsync() =>
        SendAsync<ReservationApplicationOptionsDto>("GET", "/api/reservations/application-options", null);

    public Task<ResourceAssignmentProposal> ProposeResourcesAsync(ResourceSchedulerProposalRequest request) =>
        SendAsync<ResourceAssignmentProposal>("POST", "/api/resource-scheduler/propose", request);

    public Task<ReservationDetailDto> SubmitAsync(Guid id) => TransitionAsync(id, "submit");
    public Task<ReservationDetailDto> ApproveAsync(Guid id) => TransitionAsync(id, "approve");
    public Task<ReservationDetailDto> CheckoutAsync(Guid id) => TransitionAsync(id, "checkout");
    public Task<ReservationDetailDto> ReturnAsync(Guid id) => TransitionAsync(id, "return");
    public Task<ReservationDetailDto> RejectAsync(Guid id, string reason) =>
        SendAsync<ReservationDetailDto>("POST", $"/api/reservations/{id}/reject", new ReservationTransitionRequest { Reason = reason });

    public Task<ReservationDetailDto> CancelAsync(Guid id, string? reason) =>
        SendAsync<ReservationDetailDto>("POST", $"/api/reservations/{id}/cancel", new ReservationTransitionRequest { Reason = reason });

    public async Task<DateTime> LocalInputToUtcAsync(string value)
    {
        var module = await GetModuleAsync();
        var iso = await module.InvokeAsync<string?>("localInputToUtc", value);
        if (string.IsNullOrWhiteSpace(iso)
            || !DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result))
            throw new InvalidOperationException("請輸入有效的本地日期與時間。");
        return result.ToUniversalTime();
    }

    public async Task<string> UtcToLocalInputAsync(DateTime value)
    {
        var module = await GetModuleAsync();
        return await module.InvokeAsync<string>("utcToLocalInput", value.ToUniversalTime().ToString("O"));
    }

    public async Task<bool> ConfirmAsync(string message)
    {
        var module = await GetModuleAsync();
        return await module.InvokeAsync<bool>("confirmAction", message);
    }

    public async Task<string?> PromptReasonAsync(string message)
    {
        var module = await GetModuleAsync();
        return await module.InvokeAsync<string?>("promptReason", message);
    }

    public async Task<string> FormatLocalAsync(DateTime value)
    {
        var module = await GetModuleAsync();
        return await module.InvokeAsync<string>("formatUtc", value.ToUniversalTime().ToString("O"));
    }

    public async Task ShowDateTimePickerAsync(ElementReference element)
    {
        var module = await GetModuleAsync();
        await module.InvokeVoidAsync("showDateTimePicker", element);
    }

    private Task<ReservationDetailDto> TransitionAsync(Guid id, string action) =>
        SendAsync<ReservationDetailDto>("POST", $"/api/reservations/{id}/{action}", null);

    private async Task<T> SendAsync<T>(string method, string url, object? body)
    {
        var module = await GetModuleAsync();
        var response = await module.InvokeAsync<BrowserApiResponse>("request", method, url, body);
        if (response.Status is >= 200 and < 300)
        {
            return JsonSerializer.Deserialize<T>(response.Body, JsonOptions)
                ?? throw new InvalidOperationException("伺服器回傳空白資料。");
        }

        throw new ReservationApiException(response.Status, FriendlyMessage(response.Status, response.Body));
    }

    private async Task<IJSObjectReference> GetModuleAsync() =>
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/reservation-api.js");

    private static string FriendlyMessage(int status, string body)
    {
        var serverMessage = ExtractError(body);
        if (status == 403) return "您沒有權限存取或操作這筆設備預約。";
        if (status == 404) return "找不到設備預約資料。";
        if (status == 409)
        {
            if (serverMessage.Contains("overlap", StringComparison.OrdinalIgnoreCase))
                return "此設備在所選時間已被其他人預約，請重新選擇時間或設備。";
            if (serverMessage.Contains("not bookable", StringComparison.OrdinalIgnoreCase))
                return "選取的設備目前已不可預約，請重新選擇設備。";
            if (serverMessage.Contains("transition", StringComparison.OrdinalIgnoreCase)
                || serverMessage.Contains("Draft", StringComparison.OrdinalIgnoreCase))
                return "預約狀態已變更，請重新整理後再操作。";
        }

        return string.IsNullOrWhiteSpace(serverMessage)
            ? $"伺服器處理失敗（{status}）。"
            : serverMessage;
    }

    private static string ExtractError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error)) return error.GetString() ?? string.Empty;
            if (document.RootElement.TryGetProperty("title", out var title)) return title.GetString() ?? string.Empty;
        }
        catch (JsonException)
        {
            // Preserve a non-JSON server response for diagnostics.
        }
        return body;
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is null) return;

        try
        {
            await _module.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // The interactive server circuit has already disconnected; disposal is complete.
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

    private sealed class BrowserApiResponse
    {
        [JsonPropertyName("status")] public int Status { get; set; }
        [JsonPropertyName("body")] public string Body { get; set; } = string.Empty;
    }
}

public sealed class ReservationApiException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
