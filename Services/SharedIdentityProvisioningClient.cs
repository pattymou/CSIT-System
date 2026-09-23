using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using SIT.DepartmentSystem.Web.Models;

namespace SIT.DepartmentSystem.Web.Services;

public sealed class SharedIdentityProvisioningException(
    string message,
    HttpStatusCode? statusCode = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

public sealed class SharedIdentityProvisioningClient(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<SharedIdentityProvisioningClient> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<InternalAccountProvisioningResult> CreateAsync(
        InternalAccountProvisioningRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = httpClientFactory.CreateClient(nameof(SharedIdentityProvisioningClient));
            var token = await GetTokenAsync(client, "identity.provision", cancellationToken);
            using var message = new HttpRequestMessage(HttpMethod.Post, "internal/management/csit/accounts")
            {
                Content = JsonContent.Create(request, options: Json)
            };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw await ToExceptionAsync(response, request.CorrelationId, cancellationToken);
            return await response.Content.ReadFromJsonAsync<InternalAccountProvisioningResult>(Json,
                       cancellationToken)
                   ?? throw new SharedIdentityProvisioningException("Shared Identity returned an empty response.");
        }
        catch (SharedIdentityProvisioningException) { throw; }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Shared Identity provisioning timed out. CorrelationId {CorrelationId}",
                request.CorrelationId);
            throw new SharedIdentityProvisioningException(
                "Shared Identity request timed out. Retry uses the same operation key.", null, exception);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Shared Identity provisioning unavailable. CorrelationId {CorrelationId}",
                request.CorrelationId);
            throw new SharedIdentityProvisioningException("Shared Identity is currently unavailable.", null,
                exception);
        }
    }

    public async Task<IReadOnlyList<SharedIdentityAccountListItem>> ListAccountsAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = httpClientFactory.CreateClient(nameof(SharedIdentityProvisioningClient));
            var token = await GetTokenAsync(client, "identity.accounts.read", cancellationToken);
            using var message = new HttpRequestMessage(HttpMethod.Get, "internal/management/csit/accounts");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new SharedIdentityProvisioningException(
                    "Shared Identity account list is unavailable.", response.StatusCode);
            return await response.Content.ReadFromJsonAsync<List<SharedIdentityAccountListItem>>(Json,
                       cancellationToken)
                   ?? throw new SharedIdentityProvisioningException(
                       "Shared Identity returned an empty account list response.");
        }
        catch (SharedIdentityProvisioningException) { throw; }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Shared Identity account list timed out.");
            throw new SharedIdentityProvisioningException(
                "Shared Identity account list timed out.", null, exception);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Shared Identity account list is unavailable.");
            throw new SharedIdentityProvisioningException(
                "Shared Identity is currently unavailable.", null, exception);
        }
    }

    public async Task<InternalAccountManagementResult> UpdateAccountAsync(
        Guid identityUserId,
        InternalAccountManagementRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = httpClientFactory.CreateClient(nameof(SharedIdentityProvisioningClient));
            var token = await GetTokenAsync(client, "identity.accounts.manage", cancellationToken);
            using var message = new HttpRequestMessage(HttpMethod.Put,
                $"internal/management/csit/accounts/{identityUserId}")
            {
                Content = JsonContent.Create(request, options: Json)
            };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw await ToExceptionAsync(response, request.CorrelationId, cancellationToken);
            return await response.Content.ReadFromJsonAsync<InternalAccountManagementResult>(Json,
                       cancellationToken)
                   ?? throw new SharedIdentityProvisioningException(
                       "Shared Identity returned an empty account update response.");
        }
        catch (SharedIdentityProvisioningException) { throw; }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Shared Identity account update timed out. CorrelationId {CorrelationId}",
                request.CorrelationId);
            throw new SharedIdentityProvisioningException("Shared Identity account update timed out.", null,
                exception);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception,
                "Shared Identity account update is unavailable. CorrelationId {CorrelationId}",
                request.CorrelationId);
            throw new SharedIdentityProvisioningException("Shared Identity is currently unavailable.", null,
                exception);
        }
    }

    public async Task<InternalAccountProvisioningResult> ReissueTemporaryPasswordAsync(
        Guid identityUserId,
        TemporaryPasswordReissueRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = httpClientFactory.CreateClient(nameof(SharedIdentityProvisioningClient));
            var token = await GetTokenAsync(client, "identity.provision", cancellationToken);
            using var message = new HttpRequestMessage(HttpMethod.Post,
                $"internal/management/csit/accounts/{identityUserId}/temporary-password")
            {
                Content = JsonContent.Create(request, options: Json)
            };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw await ToExceptionAsync(response, request.CorrelationId, cancellationToken);
            return await response.Content.ReadFromJsonAsync<InternalAccountProvisioningResult>(Json,
                       cancellationToken)
                   ?? throw new SharedIdentityProvisioningException(
                       "Shared Identity returned an empty password reset response.");
        }
        catch (SharedIdentityProvisioningException) { throw; }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Shared Identity password reset timed out. CorrelationId {CorrelationId}",
                request.CorrelationId);
            throw new SharedIdentityProvisioningException(
                "Shared Identity password reset timed out. Retry uses the same operation key.", null, exception);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception,
                "Shared Identity password reset is unavailable. CorrelationId {CorrelationId}",
                request.CorrelationId);
            throw new SharedIdentityProvisioningException("Shared Identity is currently unavailable.", null,
                exception);
        }
    }

    public async Task<InternalAccountManagementResult> DeprovisionAccountAsync(
        Guid identityUserId,
        AccountDeprovisionRequest request,
        CancellationToken cancellationToken = default)
    {
        var route = $"internal/management/csit/accounts/{identityUserId}/deprovision";
        try
        {
            using var client = httpClientFactory.CreateClient(nameof(SharedIdentityProvisioningClient));
            var token = await GetTokenAsync(client, "identity.accounts.manage", cancellationToken);
            using var message = new HttpRequestMessage(HttpMethod.Post, route)
            {
                Content = JsonContent.Create(request, options: Json)
            };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            logger.LogInformation(
                "Shared Identity account deletion response. Method {Method}, Route {Route}, TargetIdentityUserId {TargetIdentityUserId}, CorrelationId {CorrelationId}, StatusCode {StatusCode}",
                HttpMethod.Post.Method, route, identityUserId, request.CorrelationId,
                (int)response.StatusCode);
            if (!response.IsSuccessStatusCode)
                throw await ToExceptionAsync(response, request.CorrelationId, cancellationToken);
            var result = await response.Content.ReadFromJsonAsync<InternalAccountManagementResult>(Json,
                             cancellationToken)
                         ?? throw new SharedIdentityProvisioningException(
                             "Shared Identity returned an empty account deletion response.");
            logger.LogInformation(
                "Shared Identity account deletion result verified. TargetIdentityUserId {TargetIdentityUserId}, ResultIdentityUserId {ResultIdentityUserId}, CorrelationId {CorrelationId}, SecurityVersion {SecurityVersion}, AuthorizationVersion {AuthorizationVersion}",
                identityUserId, result.IdentityUserId, request.CorrelationId, result.SecurityVersion,
                result.AuthorizationVersion);
            return result;
        }
        catch (SharedIdentityProvisioningException) { throw; }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Shared Identity account deletion timed out. CorrelationId {CorrelationId}",
                request.CorrelationId);
            throw new SharedIdentityProvisioningException(
                "Shared Identity account deletion timed out.", null, exception);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception,
                "Shared Identity account deletion is unavailable. CorrelationId {CorrelationId}",
                request.CorrelationId);
            throw new SharedIdentityProvisioningException("Shared Identity is currently unavailable.", null,
                exception);
        }
    }

    private async Task<string> GetTokenAsync(
        HttpClient client,
        string scope,
        CancellationToken cancellationToken)
    {
        var clientId = configuration["SharedIdentity:ProvisioningClientId"];
        var clientSecret = configuration["SharedIdentity:ProvisioningClientSecret"];
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            throw new SharedIdentityProvisioningException("Shared Identity provisioning is not configured.");

        using var response = await client.PostAsync("connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["scope"] = scope
        }), cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new SharedIdentityProvisioningException("Shared Identity rejected the management client.",
                response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("access_token", out var value) ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new SharedIdentityProvisioningException("Shared Identity did not return a management token.");
        return value.GetString()!;
    }

    private static async Task<SharedIdentityProvisioningException> ToExceptionAsync(
        HttpResponseMessage response,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        var fallback = response.StatusCode switch
        {
            HttpStatusCode.BadRequest => "The account data was rejected.",
            HttpStatusCode.Conflict => "Employee number, account, or email already exists.",
            HttpStatusCode.Forbidden => "The account management actor is not authorized.",
            _ => $"Shared Identity account operation failed (correlation {correlationId})."
        };
        try
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            if (body.RootElement.TryGetProperty("error", out var error) &&
                !string.IsNullOrWhiteSpace(error.GetString()))
                fallback = error.GetString()!;
        }
        catch (JsonException) { }
        return new SharedIdentityProvisioningException(fallback, response.StatusCode);
    }
}
