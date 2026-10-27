using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace ApprovalFramework.Tests;

internal enum MockKmDeliveryBehavior
{
    Accepted,
    ServiceUnavailable,
    Timeout,
    InvalidAcceptedResponse
}

internal sealed class MockKmDeletionServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly WebApplication app;
    private readonly MockKmDeletionState state;
    private readonly ConcurrentQueue<MockKmDeliveryBehavior> behaviors = new();

    private MockKmDeletionServer(WebApplication app, MockKmDeletionState state)
    {
        this.app = app;
        this.state = state;
    }

    public HttpClient Client { get; private set; } = null!;
    public IReadOnlyCollection<Guid> DeletionRequestIds => state.Requests.Keys.ToArray();

    public static async Task<MockKmDeletionServer> StartAsync(MockKmDeletionState? state = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        var server = new MockKmDeletionServer(app, state ?? new MockKmDeletionState());
        app.MapPost(KmDeletionClient.Path,
            (Func<HttpContext, Task<IResult>>)server.AcceptAsync);
        await app.StartAsync();
        server.Client = app.GetTestClient();
        server.Client.BaseAddress = new Uri("https://km.mock.test/");
        return server;
    }

    public void Enqueue(MockKmDeliveryBehavior behavior) => behaviors.Enqueue(behavior);

    public KmDeletionCallback CreateCallback(Guid deletionRequestId, string status,
        Guid? callbackId = null, bool cleanupVerified = true,
        string cleanupOverride = "Deleted", bool retryable = true)
    {
        if (!state.Requests.TryGetValue(deletionRequestId, out var persisted))
            throw new InvalidOperationException("Mock KM has not accepted this deletion request.");
        var now = DateTimeOffset.UtcNow;
        var cleanup = new KmDeletionCleanup("Deleted", "Deleted", "Deleted", cleanupOverride,
            "Deleted", "Deleted", "NotPresent");
        return new KmDeletionCallback("1.0", callbackId ?? Guid.NewGuid(), "CSIT",
            deletionRequestId, persisted.JobId, persisted.Request.Resource, status,
            new KmDeletionTimestamps(now,
                status == "Completed" ? now.AddSeconds(2) : null,
                status == "Failed" ? now.AddSeconds(2) : null),
            status == "Completed"
                ? new KmDeletionResult(cleanupVerified, now.AddSeconds(1), cleanup) : null,
            status == "Failed"
                ? new KmDeletionError("MOCK_TEMPORARY", "Synthetic KM failure.", retryable) : null);
    }

    public static async Task<HttpResponseMessage> SendCallbackAsync(HttpClient csit,
        KmDeletionCallback callback, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/api/integrations/km/v1/document-deletion-status")
        {
            Content = JsonContent.Create(callback)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "mock-km-callback");
        return await csit.SendAsync(request, cancellationToken);
    }

    private async Task<IResult> AcceptAsync(HttpContext context)
    {
        if (!string.Equals(context.Request.Headers.Authorization,
                "Bearer mock-km-submit", StringComparison.Ordinal))
            return Results.Unauthorized();
        KmDeletionRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync<KmDeletionRequest>(Json,
                context.RequestAborted);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "invalid_json" });
        }
        if (request is null || !request.IsValid() ||
            !Guid.TryParse(context.Request.Headers["Idempotency-Key"], out var key) ||
            key != request.DeletionRequestId)
            return Results.BadRequest(new { error = "invalid_request" });

        var behavior = behaviors.TryDequeue(out var queued)
            ? queued : MockKmDeliveryBehavior.Accepted;
        if (behavior == MockKmDeliveryBehavior.ServiceUnavailable)
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        if (behavior == MockKmDeliveryBehavior.Timeout)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
        }
        if (behavior == MockKmDeliveryBehavior.InvalidAcceptedResponse)
            return Results.Json(new KmDeletionAccepted(Guid.NewGuid(), "KM-MOCK-INVALID", "Accepted"),
                statusCode: StatusCodes.Status202Accepted);

        var payload = JsonSerializer.Serialize(request, Json);
        MockKmPersistedRequest persisted;
        if (state.Requests.TryGetValue(request.DeletionRequestId, out var existing))
        {
            if (!string.Equals(existing.Payload, payload, StringComparison.Ordinal))
                return Results.Conflict();
            persisted = existing;
        }
        else
        {
            persisted = new MockKmPersistedRequest(request,
                $"KM-MOCK-{request.DeletionRequestId:N}", payload);
            if (!state.Requests.TryAdd(request.DeletionRequestId, persisted))
            {
                existing = state.Requests[request.DeletionRequestId];
                if (!string.Equals(existing.Payload, payload, StringComparison.Ordinal))
                    return Results.Conflict();
                persisted = existing;
            }
        }
        return Results.Json(new KmDeletionAccepted(request.DeletionRequestId,
            persisted.JobId, "Accepted"), statusCode: StatusCodes.Status202Accepted);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.DisposeAsync();
    }
}

internal sealed class MockKmDeletionState
{
    internal ConcurrentDictionary<Guid, MockKmPersistedRequest> Requests { get; } = new();
}

internal sealed record MockKmPersistedRequest(KmDeletionRequest Request, string JobId,
    string Payload);

internal sealed class CsitDeletionCallbackTestServer : IAsyncDisposable
{
    private readonly WebApplication app;

    private CsitDeletionCallbackTestServer(WebApplication app)
    {
        this.app = app;
        Client = app.GetTestClient();
        Client.BaseAddress = new Uri("https://csit.mock.test/");
    }

    public HttpClient Client { get; }

    public static async Task<CsitDeletionCallbackTestServer> StartAsync(
        IDbContextFactory<AppDbContext> dbFactory)
    {
        const string scheme = "IsolatedMockKmCallback";
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(dbFactory);
        builder.Services.AddScoped<KmDeletionCallbackService>();
        builder.Services.AddAuthentication(scheme)
            .AddScheme<AuthenticationSchemeOptions, MockKmCallbackAuthenticationHandler>(scheme, _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy(
            KmIntegrationAuthorization.DeletionStatusWritePolicy,
            policy => policy.AddAuthenticationSchemes(scheme).RequireAuthenticatedUser()));
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapPost("/api/integrations/km/v1/document-deletion-status",
            async (KmDeletionCallback callback, KmDeletionCallbackService service,
                CancellationToken cancellationToken) =>
                await service.ApplyAsync(callback, cancellationToken) switch
                {
                    KmDeletionCallbackOutcome.Accepted => Results.NoContent(),
                    KmDeletionCallbackOutcome.Invalid => Results.BadRequest(),
                    KmDeletionCallbackOutcome.NotFound => Results.NotFound(),
                    _ => Results.Conflict()
                }).RequireAuthorization(KmIntegrationAuthorization.DeletionStatusWritePolicy);
        await app.StartAsync();
        return new CsitDeletionCallbackTestServer(app);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.DisposeAsync();
    }
}

internal sealed class MockKmCallbackAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!string.Equals(Request.Headers.Authorization, "Bearer mock-km-callback",
                StringComparison.Ordinal))
            return Task.FromResult(AuthenticateResult.Fail("Invalid isolated test token."));
        var identity = new ClaimsIdentity([new Claim("sub", "isolated-mock-km")], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
