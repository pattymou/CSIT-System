using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public sealed class KmDeletionOptions
{
    public const string SectionName = "KmDeletion";
    public bool Enabled { get; set; }
    public string? Scope { get; set; }
}

public interface IKmDeletionAccessTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);
}

public sealed class KmDeletionAccessTokenProvider : IKmDeletionAccessTokenProvider
{
    private readonly ClientCredentialsKmIntegrationAccessTokenProvider inner;

    public KmDeletionAccessTokenProvider(IHttpClientFactory clients,
        IOptions<KmIntegrationM2MOptions> existing,
        IOptions<KmDeletionOptions> deletion, TimeProvider clock)
    {
        var source = existing.Value;
        inner = new(clients, Options.Create(new KmIntegrationM2MOptions
        {
            Authority = source.Authority, ClientId = source.ClientId,
            TokenEndpoint = source.TokenEndpoint, Audience = source.Audience,
            ClientSecretFile = source.ClientSecretFile,
            Scope = deletion.Value.Scope
        }), clock);
    }

    public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) =>
        inner.GetAccessTokenAsync(cancellationToken);
}

public interface IKmDeletionClient
{
    Task<KmDeletionDeliveryResult> SubmitAsync(KmDeletionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class KmDeletionClient(
    HttpClient http,
    IOptions<KmIntegrationOptions> integration,
    IOptions<KmDeletionOptions> configured,
    IKmDeletionAccessTokenProvider tokens) : IKmDeletionClient
{
    public const string Path = "/api/integrations/csit/v1/document-deletions";

    public async Task<KmDeletionDeliveryResult> SubmitAsync(KmDeletionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!request.IsValid()) return KmDeletionDeliveryResult.Failure(false, "INVALID_REQUEST");
        if (!configured.Value.Enabled || string.IsNullOrWhiteSpace(configured.Value.Scope))
            return KmDeletionDeliveryResult.Failure(false, "DISABLED");
        var options = integration.Value;
        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttps || baseUri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment) ||
            options.TimeoutSeconds < 1)
            return KmDeletionDeliveryResult.Failure(false, "CONFIGURATION");

        string token;
        try { token = await tokens.GetAccessTokenAsync(cancellationToken); }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        { return KmDeletionDeliveryResult.Failure(true, "AUTH_UNAVAILABLE"); }
        if (string.IsNullOrWhiteSpace(token))
            return KmDeletionDeliveryResult.Failure(false, "AUTH_UNAVAILABLE");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, Path))
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        message.Headers.Add("Idempotency-Key", request.DeletionRequestId.ToString("D"));
        try
        {
            using var response = await http.SendAsync(message,
                HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                KmDeletionAccepted? accepted;
                try { accepted = await response.Content.ReadFromJsonAsync<KmDeletionAccepted>(deadline.Token); }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                { return KmDeletionDeliveryResult.Failure(false, "INVALID_RESPONSE"); }
                return accepted is not null &&
                    accepted.DeletionRequestId == request.DeletionRequestId &&
                    accepted.Status == "Accepted" && accepted.KmDeletionJobId is
                        { Length: > 0 and <= 150 }
                    ? KmDeletionDeliveryResult.Success(accepted.KmDeletionJobId)
                    : KmDeletionDeliveryResult.Failure(false, "INVALID_RESPONSE");
            }
            var retryable = (int)response.StatusCode >= 500 ||
                response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
            return KmDeletionDeliveryResult.Failure(retryable, "HTTP_" + (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return KmDeletionDeliveryResult.Failure(true, "TIMEOUT"); }
        catch (HttpRequestException)
        { return KmDeletionDeliveryResult.Failure(true, "CONNECTION"); }
    }
}
