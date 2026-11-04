using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Services.Approval;

public enum MySubmissionSourceKind
{
    ApprovalSubmission,
    VerificationApplication
}

public enum MySubmissionView
{
    Pending,
    Returned
}

public sealed record MySubmissionWorkspaceCounts(int Pending, int Returned);

public sealed record MySubmissionWorkspaceItemDto(
    Guid Id,
    MySubmissionSourceKind SourceKind,
    string SourceTitle,
    string ItemLabel,
    string? ItemRoute,
    string FileLabel,
    string SubmittedByAccount,
    DateTime SubmittedAt,
    string? ReturnReason,
    string? FileDownloadRoute,
    string PrimaryActionLabel);

public sealed record MySubmissionWorkspacePage(
    MySubmissionWorkspaceCounts Counts,
    IReadOnlyList<MySubmissionWorkspaceItemDto> Items,
    int Page,
    int PageSize,
    int TotalCount);

/// <summary>
/// Read-only, account-scoped projection over the independent document approval and verification workflows.
/// </summary>
public sealed class MySubmissionQueryService(
    IDbContextFactory<AppDbContext> dbFactory,
    ILogger<MySubmissionQueryService> logger)
{
    public async Task<MySubmissionWorkspacePage> ListAsync(
        ClaimsPrincipal user,
        MySubmissionView view,
        int page = 1,
        int pageSize = 200,
        CancellationToken cancellationToken = default)
    {
        var account = ApprovalIdentity.Account(user);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var skip = (int)Math.Min(int.MaxValue, (long)(page - 1) * pageSize);
        var candidateLimit = (int)Math.Min(int.MaxValue, (long)skip + pageSize);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var pendingDocuments = db.PendingSubmissions.AsNoTracking().Where(x =>
            x.SubmittedByAccount == account && x.Status == ApprovalStatus.Pending);
        var returnedDocuments = db.PendingSubmissions.AsNoTracking().Where(x =>
            x.SubmittedByAccount == account && x.Status == ApprovalStatus.Rejected &&
            x.ResubmittedAsSubmissionId == null && x.SubmitterDismissedAt == null);
        var submittedApplications = db.VerificationApplications.AsNoTracking().Where(x =>
            x.ApplicantAccount == account && x.Status == VerificationApplicationStatus.Submitted);
        var returnedApplications = db.VerificationApplications.AsNoTracking().Where(x =>
            x.ApplicantAccount == account && x.Status == VerificationApplicationStatus.Returned);

        var pendingDocumentCount = await pendingDocuments.CountAsync(cancellationToken);
        var returnedDocumentCount = await returnedDocuments.CountAsync(cancellationToken);
        var submittedApplicationCount = await submittedApplications.CountAsync(cancellationToken);
        var returnedApplicationCount = await returnedApplications.CountAsync(cancellationToken);
        var counts = new MySubmissionWorkspaceCounts(
            pendingDocumentCount + submittedApplicationCount,
            returnedDocumentCount + returnedApplicationCount);

        var documentQuery = view == MySubmissionView.Pending ? pendingDocuments : returnedDocuments;
        var applicationQuery = view == MySubmissionView.Pending ? submittedApplications : returnedApplications;
        var documents = await documentQuery.OrderByDescending(x => x.SubmittedAt)
            .ThenByDescending(x => x.Id).Take(candidateLimit).ToListAsync(cancellationToken);
        var applications = await applicationQuery.OrderByDescending(x => x.SubmittedAt)
            .ThenByDescending(x => x.Id).Take(candidateLimit)
            .Select(x => new
            {
                x.Id,
                x.ApplicationNo,
                x.ProductModel,
                x.ApplicantAccount,
                x.SubmittedAt,
                x.UpdatedAt,
                x.ProcessingNote,
                x.ValidationFormPayloadJson
            })
            .ToListAsync(cancellationToken);

        var menuCodes = documents.Select(x => x.SourceMenuCode).Distinct().ToArray();
        var menuRows = await db.MenuItems.AsNoTracking().Where(x => menuCodes.Contains(x.Code))
            .Select(x => new { x.Code, x.Title }).ToListAsync(cancellationToken);
        var menuTitles = menuRows.GroupBy(x => x.Code, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First().Title, StringComparer.Ordinal);

        var items = new List<MySubmissionWorkspaceItemDto>(documents.Count + applications.Count);
        foreach (var document in documents)
        {
            ApprovalContextNavigationResult? navigation = null;
            try
            {
                navigation = await ApprovalContextNavigation.ResolveAsync(db, document, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception,
                    "Could not resolve navigation for the authenticated user's submission {SubmissionId}.",
                    document.Id);
            }

            var label = string.IsNullOrWhiteSpace(navigation?.Display)
                ? ContextLabel(document.ContextDisplay)
                : navigation.Display;
            items.Add(new MySubmissionWorkspaceItemDto(
                document.Id,
                MySubmissionSourceKind.ApprovalSubmission,
                menuTitles.GetValueOrDefault(document.SourceMenuCode, document.SourceMenuCode),
                label,
                navigation?.Route,
                Display(document.OriginalFileName),
                document.SubmittedByAccount,
                document.SubmittedAt,
                view == MySubmissionView.Returned ? document.RejectReason : null,
                string.IsNullOrWhiteSpace(document.StagingStorageKey)
                    ? null
                    : $"/api/supervisor-submissions/{document.Id}/file",
                view == MySubmissionView.Pending ? "撤回" : "重新上傳"));
        }

        foreach (var application in applications)
        {
            var dynamicApplication = !string.IsNullOrWhiteSpace(application.ValidationFormPayloadJson);
            items.Add(new MySubmissionWorkspaceItemDto(
                application.Id,
                MySubmissionSourceKind.VerificationApplication,
                "驗證申請",
                $"{application.ApplicationNo} / {DisplayModel(application.ProductModel)}",
                view == MySubmissionView.Returned && dynamicApplication
                    ? $"/validation-requests/{application.Id}"
                    : $"/verification-applications/{application.Id}",
                "驗證申請單",
                application.ApplicantAccount,
                application.SubmittedAt ?? application.UpdatedAt,
                view == MySubmissionView.Returned ? application.ProcessingNote : null,
                null,
                view == MySubmissionView.Returned && dynamicApplication ? "返回修改" : "查看"));
        }

        var totalCount = view == MySubmissionView.Pending ? counts.Pending : counts.Returned;
        var selected = items.OrderByDescending(x => x.SubmittedAt)
            .ThenBy(x => x.SourceKind)
            .ThenByDescending(x => x.Id)
            .Skip(skip)
            .Take(pageSize)
            .ToList();
        return new MySubmissionWorkspacePage(counts, selected, page, pageSize, totalCount);
    }

    private static string ContextLabel(string? value) =>
        string.IsNullOrWhiteSpace(value) || Guid.TryParse(value, out _)
            ? "原項目（目前無法解析）"
            : value.Trim();

    private static string Display(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

    private static string DisplayModel(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "未填 Model Name" : value.Trim();
}
