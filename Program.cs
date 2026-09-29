using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Components;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Implementations;
using SIT.DepartmentSystem.Web.Services.Interfaces;
using SIT.DepartmentSystem.Web.Models.Config;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore.Authentication.OpenIdConnect", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient.SharedIdentitySessionValidator", LogLevel.Warning);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddControllers();

/* Swagger */
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();

var authenticationMode = builder.Configuration["AuthenticationMode"] ?? "Legacy";
if (authenticationMode is not ("Legacy" or "Oidc"))
    throw new InvalidOperationException("AuthenticationMode must be Legacy or Oidc.");
var useSharedIdentity = authenticationMode == "Oidc";
if (useSharedIdentity && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Shared Identity P0 is enabled only in Development.");

var authentication = builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/signin";
        options.LogoutPath = "/auth/logout";
        options.AccessDeniedPath = "/signin";
        options.Cookie.Name = useSharedIdentity ? "CSIT.Dev.Oidc" : "CSIT.Legacy";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = false;
        if (useSharedIdentity) options.EventsType = typeof(SharedIdentityCookieEvents);
    });
if (useSharedIdentity)
{
    var authority = builder.Configuration["SharedIdentity:Authority"];
    var clientId = builder.Configuration["SharedIdentity:ClientId"];
    var clientSecret = builder.Configuration["SharedIdentity:ClientSecret"];
    if (string.IsNullOrWhiteSpace(authority) || !Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri) ||
        authorityUri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(clientId) ||
        string.IsNullOrWhiteSpace(clientSecret))
        throw new InvalidOperationException("Shared Identity DEV configuration is incomplete.");
    builder.Services.AddScoped<SharedIdentityClaimsAdapter>();
    builder.Services.AddScoped<SharedIdentityCookieEvents>();
    builder.Services.AddHttpClient<SharedIdentitySessionValidator>(http => http.Timeout = TimeSpan.FromSeconds(5));
    authentication.AddOpenIdConnect("oidc", options =>
    {
        options.Authority = authority;
        options.ClientId = clientId;
        options.ClientSecret = clientSecret;
        options.ResponseType = "code";
        options.UsePkce = true;
        options.RequireHttpsMetadata = true;
        options.CallbackPath = "/signin-oidc";
        options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.SaveTokens = false;
        options.MapInboundClaims = false;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidAudience = clientId
        };
        options.Events = new OpenIdConnectEvents
        {
            OnTokenValidated = async context =>
            {
                var adapter = context.HttpContext.RequestServices.GetRequiredService<SharedIdentityClaimsAdapter>();
                var mapped = context.Principal is null ? null :
                    await adapter.MapAsync(context.Principal, context.HttpContext.RequestAborted);
                if (mapped is null) context.Fail("Shared Identity account or CSIT access rejected.");
                else
                {
                    context.Principal = mapped;
                    if (context.Properties?.RedirectUri == "/home" &&
                        mapped.HasClaim(SystemAuthorization.AccessScopeClaim,
                            SystemAuthorization.AccessScopes.RdApplicant))
                        context.Properties.RedirectUri = "/rd";
                }
            },
            OnRemoteFailure = context =>
            {
                context.HandleResponse();
                context.Response.Redirect("/signin?error=1");
                return Task.CompletedTask;
            },
            OnAuthenticationFailed = context =>
            {
                context.HandleResponse();
                context.Response.Redirect("/signin?error=1");
                return Task.CompletedTask;
            }
        };
    });
}

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(SystemAuthorization.Policies.RdApplicant, policy =>
        policy.RequireAuthenticatedUser()
            .RequireClaim(SystemAuthorization.AccessScopeClaim, SystemAuthorization.AccessScopes.RdApplicant));
    options.AddPolicy(SystemAuthorization.Policies.CsitStaff, policy =>
        policy.RequireAuthenticatedUser()
            .RequireClaim(SystemAuthorization.AccessScopeClaim, SystemAuthorization.AccessScopes.CsitStaff));
    options.AddPolicy(SystemAuthorization.Policies.ReservationUser, policy =>
        policy.RequireAuthenticatedUser().RequireAssertion(context =>
            context.User.HasClaim(SystemAuthorization.AccessScopeClaim, SystemAuthorization.AccessScopes.RdApplicant)
            || context.User.HasClaim(SystemAuthorization.AccessScopeClaim, SystemAuthorization.AccessScopes.CsitStaff)));
    options.AddPolicy(SystemAuthorization.Policies.Administration, policy =>
        policy.RequireAuthenticatedUser()
            .AddRequirements(new AdministrationAccessRequirement()));
    options.AddPolicy(SystemAuthorization.Policies.BusinessWrite, policy =>
        policy.RequireAuthenticatedUser()
            .AddRequirements(new BusinessWriteRequirement()));
});

builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

builder.Services.AddScoped<IModuleService, ModuleService>();
builder.Services.AddScoped<IModuleRecordService, ModuleRecordService>();
builder.Services.AddScoped<IModuleRecordCreationService, ModuleRecordCreationService>();
builder.Services.AddScoped<IVerificationApplicationService, VerificationApplicationService>();
builder.Services.AddScoped<IReservationService, ReservationService>();
builder.Services.AddScoped<IReservationPolicyService, ReservationPolicyService>();
builder.Services.AddScoped<IApparatusAvailabilityService, ApparatusAvailabilityService>();
builder.Services.AddScoped<IApparatusResourceCapabilityService, ApparatusResourceCapabilityService>();
builder.Services.AddScoped<IResourceSchedulerService, ResourceSchedulerService>();
builder.Services.AddScoped<ReservationApiClient>();
builder.Services.AddScoped<BrowserApiClient>();
builder.Services.AddScoped<ITestCatalogService, TestCatalogService>();
builder.Services.AddScoped<IEnvironmentGroupDeviceService, EnvironmentGroupDeviceService>();
builder.Services.AddScoped<IEnvironmentReadinessService, EnvironmentReadinessService>();
builder.Services.AddScoped<IEnvironmentAvailabilityService, EnvironmentAvailabilityService>();
builder.Services.AddScoped<IPlannedTestItemService, PlannedTestItemService>();
builder.Services.AddScoped<IModuleCaseService, ModuleCaseService>();
builder.Services.AddScoped<IModuleTaskService, ModuleTaskService>();
builder.Services.AddScoped<AssignableEngineerDirectory>();
builder.Services.AddScoped<LabOwnerDirectory>();
builder.Services.AddScoped<IMenuManagementService, MenuManagementService>();
builder.Services.AddScoped<IAuthorizationHandler, AdministrationAccessHandler>();
builder.Services.AddSingleton<IAuthorizationHandler, BusinessWriteHandler>();
builder.Services.AddScoped<IBusinessWriteAuthorizationGuard, BusinessWriteAuthorizationGuard>();
builder.Services.AddScoped<ProvisioningActorResolver>();
builder.Services.AddScoped<InternalAccountCreationService>();
builder.Services.AddScoped<SharedIdentityProvisioningClient>();
builder.Services.AddHttpClient(nameof(SharedIdentityProvisioningClient), (services, http) =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    var authority = configuration["SharedIdentity:Authority"];
    if (!Uri.TryCreate(authority, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        throw new InvalidOperationException("Shared Identity provisioning authority must be HTTPS.");
    http.BaseAddress = uri;
    http.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddScoped<AdAuthenticationService>();

builder.Services.Configure<UploadSettings>(
    builder.Configuration.GetSection("UploadSettings"));

builder.Services.Configure<ApparatusSettings>(
    builder.Configuration.GetSection("Apparatus"));

builder.Services.AddScoped<IApparatusService, ApparatusService>();
builder.Services.AddScoped<ICaseFileService, CaseFileService>();

builder.Services.Configure<RawDataSettings>(
    builder.Configuration.GetSection("RawData"));

builder.Services.AddScoped<IRawDataExportService, RawDataExportService>();
builder.Services.AddScoped<ISystemOptionService, SystemOptionService>();

builder.Services.AddScoped(sp =>
{
    var navigationManager = sp.GetRequiredService<NavigationManager>();
    return new HttpClient
    {
        BaseAddress = new Uri(navigationManager.BaseUri)
    };
});

/*
 * 這段重複了，可以刪掉
 * 前面已經有 AddAuthentication / AddAuthorization
 */
// builder.Services.AddAuthentication();
// builder.Services.AddAuthorization();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

/* Swagger */
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "CSIT Department System API v1");
    options.RoutePrefix = "swagger";
});

app.UseHttpsRedirection();
app.UseStaticFiles();

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/administration/accounts"))
    {
        context.Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.Expires = "0";
    }
    await next();
});

app.UseAuthentication();
app.UseAuthorization();

/* 這行一定要補 */
app.UseAntiforgery();

app.MapControllers();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
