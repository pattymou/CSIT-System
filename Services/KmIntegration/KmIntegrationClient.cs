using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public interface IKmIntegrationClient
{
    Task<KmDeliveryResult> SubmitAsync(KmIngestionRequest request, CancellationToken cancellationToken);
    Task<KmDeliveryResult> SubmitDocumentLibraryAsync(
        DocumentLibraryIngestionRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(KmDeliveryResult.Failure(false, "UNSUPPORTED", "Document library delivery is unavailable."));
    Task<KmDeliveryResult> SubmitVendorFileAsync(
        VendorFileIngestionRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(KmDeliveryResult.Failure(false, "VENDOR_CONTRACT_NOT_APPROVED",
            "Anderson KM Vendor ingestion contract is not approved."));
}

public interface IKmIntegrationAccessTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);
}

// Replaced by the service-to-service Client Credentials foundation when it is delivered.
public sealed class UnavailableKmIntegrationAccessTokenProvider : IKmIntegrationAccessTokenProvider
{
    public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("KM Client Credentials foundation is not configured.");
}

public sealed class KmIntegrationClient(
    HttpClient http,
    IOptions<KmIntegrationOptions> configured,
    IKmIntegrationAccessTokenProvider tokens) : IKmIntegrationClient
{
    public const string IngestionPath = "/api/integrations/csit/v1/ingestion-jobs";
    public const string DocumentLibraryIngestionPath = "/api/integrations/csit/v1/document-library/jobs";

    public async Task<KmDeliveryResult> SubmitAsync(KmIngestionRequest request, CancellationToken cancellationToken)
    {
        var options = configured.Value;
        if (!options.Enabled)
            return KmDeliveryResult.Failure(false, "DISABLED", "KM integration is disabled.");
        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttps || baseUri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(baseUri.Query) ||
            !string.IsNullOrEmpty(baseUri.Fragment) || options.TimeoutSeconds <= 0)
            return KmDeliveryResult.Failure(false, "CONFIGURATION", "KM integration configuration is incomplete.");

        string accessToken;
        try { accessToken = await tokens.GetAccessTokenAsync(cancellationToken); }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return KmDeliveryResult.Failure(false, "AUTH_NOT_CONFIGURED", "KM Client Credentials is unavailable.");
        }
        if (string.IsNullOrWhiteSpace(accessToken))
            return KmDeliveryResult.Failure(false, "AUTH_NOT_CONFIGURED", "KM Client Credentials is unavailable.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, IngestionPath))
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        message.Headers.Add("Idempotency-Key", request.EventId.ToString("D"));
        try
        {
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                KmIngestionResponse? payload;
                try { payload = await response.Content.ReadFromJsonAsync<KmIngestionResponse>(timeout.Token); }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    return KmDeliveryResult.Failure(false, "INVALID_RESPONSE", "KM 202 response is invalid.");
                }
                if (payload is null || string.IsNullOrWhiteSpace(payload.JobId) ||
                    string.IsNullOrWhiteSpace(payload.Status) || payload.SourceSystem != request.SourceSystem ||
                    payload.DocumentId != request.DocumentId || payload.Version != request.Version)
                    return KmDeliveryResult.Failure(false, "INVALID_RESPONSE", "KM 202 response does not match the event.");
                return KmDeliveryResult.Success(payload.JobId);
            }
            var retryable = (int)response.StatusCode >= 500 ||
                response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout;
            return KmDeliveryResult.Failure(retryable, "HTTP_" + (int)response.StatusCode,
                retryable ? "KM is temporarily unavailable." : "KM rejected the ingestion request.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return KmDeliveryResult.Failure(true, "TIMEOUT", "KM ingestion request timed out.");
        }
        catch (HttpRequestException)
        {
            return KmDeliveryResult.Failure(true, "CONNECTION", "KM ingestion connection failed.");
        }
    }

    public async Task<KmDeliveryResult> SubmitDocumentLibraryAsync(
        DocumentLibraryIngestionRequest request,
        CancellationToken cancellationToken)
    {
        var options = configured.Value;
        if (!options.Enabled)
            return KmDeliveryResult.Failure(false, "DISABLED", "KM integration is disabled.");
        if (!TryGetBaseUri(options, out var baseUri))
            return KmDeliveryResult.Failure(false, "CONFIGURATION", "KM integration configuration is incomplete.");

        string accessToken;
        try { accessToken = await tokens.GetAccessTokenAsync(cancellationToken); }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return KmDeliveryResult.Failure(false, "AUTH_NOT_CONFIGURED", "KM Client Credentials is unavailable.");
        }
        if (string.IsNullOrWhiteSpace(accessToken))
            return KmDeliveryResult.Failure(false, "AUTH_NOT_CONFIGURED", "KM Client Credentials is unavailable.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        using var message = new HttpRequestMessage(HttpMethod.Post,
            new Uri(baseUri!, DocumentLibraryIngestionPath))
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        message.Headers.Add("Idempotency-Key", request.CsitJobId.ToString("D"));
        try
        {
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                DocumentLibraryIngestionResponse? payload;
                try
                {
                    payload = await response.Content.ReadFromJsonAsync<DocumentLibraryIngestionResponse>(timeout.Token);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    return KmDeliveryResult.Failure(false, "INVALID_RESPONSE", "KM 202 response is invalid.");
                }
                if (payload is null || payload.CsitJobId != request.CsitJobId ||
                    string.IsNullOrWhiteSpace(payload.KmJobId) || payload.Status != "Accepted")
                    return KmDeliveryResult.Failure(false, "INVALID_RESPONSE",
                        "KM 202 response does not match the document library job.");
                return KmDeliveryResult.Success(payload.KmJobId);
            }
            var retryable = (int)response.StatusCode >= 500 ||
                response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout;
            return KmDeliveryResult.Failure(retryable, "HTTP_" + (int)response.StatusCode,
                retryable ? "KM is temporarily unavailable." : "KM rejected the document library job.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return KmDeliveryResult.Failure(true, "TIMEOUT", "KM document library request timed out.");
        }
        catch (HttpRequestException)
        {
            return KmDeliveryResult.Failure(true, "CONNECTION", "KM document library connection failed.");
        }
    }

    public Task<KmDeliveryResult> SubmitVendorFileAsync(
        VendorFileIngestionRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(KmDeliveryResult.Failure(false, "VENDOR_CONTRACT_NOT_APPROVED",
            "Anderson KM Vendor ingestion contract is not approved."));

    private static bool TryGetBaseUri(KmIntegrationOptions options, out Uri? baseUri)
    {
        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttps || baseUri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment) ||
            options.TimeoutSeconds <= 0)
        {
            baseUri = null;
            return false;
        }
        return true;
    }
}
