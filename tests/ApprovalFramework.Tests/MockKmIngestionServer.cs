using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SIT.DepartmentSystem.Web.Controllers;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace ApprovalFramework.Tests;

internal sealed class MockKmIngestionServer : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly object responseLock = new();
    private HttpStatusCode? nextFailure;
    private TimeSpan? nextDelay;

    private MockKmIngestionServer(WebApplication app)
    {
        this.app = app;
    }

    public HttpClient Client { get; private set; } = null!;
    public KmIngestionRequest? LastRequest { get; private set; }
    public string JobId { get; private set; } = "mock-job-unassigned";

    public void FailNext(HttpStatusCode status)
    {
        lock (responseLock) nextFailure = status;
    }

    public void DelayNext(TimeSpan delay)
    {
        lock (responseLock) nextDelay = delay;
    }

    public static async Task<MockKmIngestionServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        var server = new MockKmIngestionServer(app);
        app.MapPost(KmIntegrationClient.IngestionPath, async (HttpRequest request, KmIngestionRequest payload) =>
        {
            if (!string.Equals(request.Headers.Authorization, "Bearer mock-csit-token", StringComparison.Ordinal))
                return Results.Unauthorized();
            if (!Guid.TryParse(request.Headers["Idempotency-Key"], out var key) || key != payload.EventId)
                return Results.BadRequest(new { error = "invalid_idempotency_key" });
            Interlocked.Increment(ref server.requestCount);
            server.LastRequest = payload;
            HttpStatusCode? failure;
            TimeSpan? delay;
            lock (server.responseLock)
            {
                failure = server.nextFailure;
                delay = server.nextDelay;
                server.nextFailure = null;
                server.nextDelay = null;
            }
            if (delay.HasValue) await Task.Delay(delay.Value, request.HttpContext.RequestAborted);
            if (failure.HasValue) return Results.StatusCode((int)failure.Value);
            var jobId = "mock-job-" + payload.EventId.ToString("N");
            server.JobId = jobId;
            return Results.Json(new KmIngestionResponse(jobId, payload.SourceSystem,
                payload.DocumentId, payload.Version, "Accepted"), statusCode: (int)HttpStatusCode.Accepted);
        });
        await app.StartAsync();
        server.Client = app.GetTestClient();
        server.Client.BaseAddress = new Uri("https://mock-km.test/");
        return server;
    }

    private int requestCount;
    public int RequestCount => Volatile.Read(ref requestCount);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.DisposeAsync();
    }
}

internal sealed class CsitKmStatusCallbackTestServer : IAsyncDisposable
{
    private readonly WebApplication app;

    private CsitKmStatusCallbackTestServer(WebApplication app)
    {
        this.app = app;
    }

    public HttpClient Client { get; private set; } = null!;

    public static async Task<CsitKmStatusCallbackTestServer> StartAsync(
        IDbContextFactory<AppDbContext> dbFactory)
    {
        const string scheme = "IsolatedMockKmCallback";
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(dbFactory);
        builder.Services.AddControllers().AddApplicationPart(typeof(KmIntegrationController).Assembly);
        builder.Services.AddAuthentication(scheme)
            .AddScheme<AuthenticationSchemeOptions, MockKmCallbackAuthenticationHandler>(scheme, _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(KmIntegrationAuthorization.Policy,
                policy => policy.AddAuthenticationSchemes(scheme).RequireAuthenticatedUser());
            options.AddPolicy(KmIntegrationAuthorization.StatusWritePolicy,
                policy => policy.AddAuthenticationSchemes(scheme).RequireAuthenticatedUser());
            options.AddPolicy(KmIntegrationAuthorization.DocumentReadPolicy,
                policy => policy.AddAuthenticationSchemes(scheme).RequireAuthenticatedUser());
        });
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        var server = new CsitKmStatusCallbackTestServer(app)
        {
            Client = app.GetTestClient()
        };
        server.Client.BaseAddress = new Uri("https://mock-csit.test/");
        return server;
    }

    public async Task<HttpResponseMessage> SendAsync(KmStatusCallback callback, bool authorize = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/api/integrations/km/v1/ingestion-status") { Content = JsonContent.Create(callback) };
        if (authorize) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "mock-km-callback");
        return await Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.DisposeAsync();
    }
}
