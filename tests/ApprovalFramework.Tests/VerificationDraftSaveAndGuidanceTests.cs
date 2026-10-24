using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using SIT.DepartmentSystem.Web.Components.Pages;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Implementations;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace ApprovalFramework.Tests;

public sealed class VerificationDraftSaveAndGuidanceTests
{
    [Fact]
    public async Task ReenteredBlankDraftSavePersistsOnlyOneDraft()
    {
        var applicationId = Guid.NewGuid();
        var applications = new BlockingApplicationService(applicationId);
        var component = new ValidationRequests();
        SetProperty(component, "Applications", applications);
        SetProperty(component, "AuthenticationStateProvider", new TestAuthenticationStateProvider());
        SetProperty(component, "Nav", new TestNavigationManager());
        SetProperty(component, "Logger", NullLogger<ValidationRequests>.Instance);
        SetField(component, "_preview", new ValidationRequestPreviewDto(
            "VALIDATION_TEST_REQUEST", Guid.NewGuid(), 1, "PRODUCT-A", "Product A", [], []));

        var firstSave = InvokeSaveAsync(component, submit: false);
        await applications.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var repeatedSave = InvokeSaveAsync(component, submit: false);
        await repeatedSave;
        applications.AllowCreate.TrySetResult();
        await firstSave;

        Assert.Equal(1, applications.PersistedDraftCount);
        Assert.Equal(applicationId, GetField<Guid?>(component, "_draftId"));
        Assert.Equal("草稿已成功儲存。", GetField<string>(component, "_operationSuccess"));
    }

    [Fact]
    public void DirectApprovalMissingDateUsesChineseGuidedValidationMetadata()
    {
        var error = Assert.Throws<TargetInvocationException>(() =>
            ValidateDirectApprovalDate(directApproval: true, expectedEndDate: null));
        var validation = Assert.IsType<VerificationApplicationValidationException>(error.InnerException);
        var issue = Assert.Single(validation.Issues);

        Assert.Equal("正式送出未完成，請在『主管自行送單免審』區域填寫『預計結束日期』後重新送出。", validation.Message);
        Assert.Equal(VerificationApplicationValidationIssue.DirectApprovalAreaCode, issue.AreaCode);
        Assert.Equal(VerificationApplicationValidationIssue.ExpectedEndDateFieldCode, issue.FieldCode);
    }

    [Fact]
    public void ExternalApplicantIsNotRequiredToProvideDirectApprovalDate()
    {
        Assert.Equal(default, ValidateDirectApprovalDate(directApproval: false, expectedEndDate: null));
    }

    [Fact]
    public void EditorKeepsFormVisibleAndTargetsExpectedEndDateWhileLoggingTechnicalFailures()
    {
        var source = Read("Components", "Pages", "ValidationRequests.razor");

        Assert.Contains("id=\"validation-field-@VerificationApplicationValidationIssue.ExpectedEndDateFieldCode\"", source);
        Assert.Contains("direct-approval-date.validation-field-focus", source);
        Assert.Contains("BuildValidationGuidance", source);
        Assert.Contains("Logger.LogError(ex", source);
        Assert.Contains("草稿儲存失敗，請稍後再試", source);
        Assert.Contains("@if (!string.IsNullOrWhiteSpace(_operationError))", source);
    }

    [Fact]
    public void WifiGuidanceStillUsesSharedAreaAndFieldMechanism()
    {
        var editor = Read("Components", "Pages", "ValidationRequests.razor");
        var preview = Read("Components", "Shared", "ValidationRequestPreview.razor");

        Assert.Contains("RequestedActiveTab=\"ValidationArea\"", editor);
        Assert.Contains("FocusFieldCode=\"ValidationField\"", editor);
        Assert.Contains("_activeTab = RequestedActiveTab", preview);
        Assert.Contains("csitScrollIntoView", editor);
    }

    private static DateOnly ValidateDirectApprovalDate(bool directApproval, DateOnly? expectedEndDate)
    {
        var method = typeof(VerificationApplicationService).GetMethod(
            "ValidateDirectApprovalDate",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (DateOnly)method.Invoke(null, [directApproval, expectedEndDate])!;
    }

    private static Task InvokeSaveAsync(ValidationRequests component, bool submit)
    {
        var method = typeof(ValidationRequests).GetMethod("SaveAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task)method.Invoke(component, [submit])!;
    }

    private static void SetProperty(object target, string name, object value) =>
        target.GetType().GetProperty(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!
            .SetValue(target, value);

    private static void SetField(object target, string name, object? value) =>
        target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);

    private static T? GetField<T>(object target, string name) =>
        (T?)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target);

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([FindRoot(), .. parts]));

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "SIT.DepartmentSystem.Web.csproj"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class TestAuthenticationStateProvider : AuthenticationStateProvider
    {
        private static readonly AuthenticationState State = new(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("account", "applicant")], "test")));

        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(State);
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("https://localhost/", "https://localhost/validation-requests");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }

    private sealed class BlockingApplicationService(Guid applicationId) : IVerificationApplicationService
    {
        public TaskCompletionSource CreateStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowCreate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int PersistedDraftCount { get; private set; }

        public async Task<VerificationApplicationDto> CreateDraftAsync(
            ClaimsPrincipal user,
            CreateVerificationApplicationRequest request,
            CancellationToken cancellationToken = default)
        {
            PersistedDraftCount++;
            CreateStarted.TrySetResult();
            await AllowCreate.Task.WaitAsync(cancellationToken);
            return new VerificationApplicationDto
            {
                Id = applicationId,
                ApplicationNo = "VA-TEST",
                Status = SIT.DepartmentSystem.Web.Entities.VerificationApplicationStatus.Draft
            };
        }

        public Task<VerificationApplicationDto> UpdateDraftAsync(Guid id, ClaimsPrincipal user, UpdateVerificationApplicationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteDraftAsync(Guid id, ClaimsPrincipal user, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> IsDirectApprovalApplicantAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<VerificationApplicationDto> SubmitAsync(Guid id, ClaimsPrincipal user, DateOnly? expectedEndDate = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VerificationApplicationDto> UpdateForLeaderReviewAsync(Guid id, ClaimsPrincipal user, UpdateVerificationApplicationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VerificationApplicationDto> ReturnAsync(Guid id, ClaimsPrincipal user, string? note, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VerificationApplicationDto> RejectAsync(Guid id, ClaimsPrincipal user, string? note, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VerificationApplicationDto> AcceptAsync(Guid id, ClaimsPrincipal user, DateOnly expectedEndDate, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VerificationApplicationDto?> GetForApplicantAsync(Guid id, ClaimsPrincipal user, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VerificationApplicationDto?> GetForLeaderReviewAsync(Guid id, ClaimsPrincipal user, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ListResponseDto<VerificationApplicationDto>> ListForApplicantAsync(ClaimsPrincipal user, int page, int pageSize, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ListResponseDto<VerificationApplicationDto>> ListForLeaderReviewAsync(ClaimsPrincipal user, int page, int pageSize, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VerificationApplicationDto?> GetForCsitAsync(Guid id, ClaimsPrincipal user, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ListResponseDto<VerificationApplicationDto>> ListForCsitAsync(ClaimsPrincipal user, int page, int pageSize, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
