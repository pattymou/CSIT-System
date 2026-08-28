using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Components;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Implementations;
using SIT.DepartmentSystem.Web.Services.Interfaces;
using SIT.DepartmentSystem.Web.Models.Config;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddControllers();

/* Swagger */
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddCascadingAuthenticationState();

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/signin";
        options.LogoutPath = "/auth/logout";
        options.AccessDeniedPath = "/signin";
    });

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
builder.Services.AddScoped<ITestCatalogService, TestCatalogService>();
builder.Services.AddScoped<IPlannedTestItemService, PlannedTestItemService>();
builder.Services.AddScoped<IModuleCaseService, ModuleCaseService>();
builder.Services.AddScoped<IModuleTaskService, ModuleTaskService>();
builder.Services.AddScoped<IMenuManagementService, MenuManagementService>();
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

app.UseAuthentication();
app.UseAuthorization();

/* 這行一定要補 */
app.UseAntiforgery();

app.MapControllers();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
