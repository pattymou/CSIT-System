using System.Data;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Models.Config;
using SIT.DepartmentSystem.Web.Services.Interfaces;
using SIT.DepartmentSystem.Web.Services.KmIntegration;
using SIT.DepartmentSystem.Web.Services.ValidationRequests;

namespace SIT.DepartmentSystem.Web.Services.Implementations;

public sealed class VerificationApplicationService : IVerificationApplicationService
{
    private const string VerificationAuditSource = "verification-applications";
    private const int AuditDetailsChunkSize = 900;
    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AppDbContext _db;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IModuleRecordCreationService _moduleRecordCreation;
    private readonly ISystemOptionService _systemOptions;
    private readonly IBusinessWriteAuthorizationGuard _businessWrite;
    private readonly IValidationRequestConfigurationService _validationRequests;
    private readonly DocumentStagingOptions _stagingOptions;
    private readonly VerificationAttachmentArchiveProcessor _attachmentArchive;
    private readonly bool _kmEnabled;
    private readonly VerificationWorkItemProvisioner _workItems;

    public VerificationApplicationService(
        AppDbContext db,
        IModuleRecordCreationService moduleRecordCreation,
        ISystemOptionService systemOptions,
        IBusinessWriteAuthorizationGuard businessWrite,
        IValidationRequestConfigurationService validationRequests,
        IOptions<DocumentStagingOptions> stagingOptions,
        IDbContextFactory<AppDbContext> dbFactory,
        VerificationAttachmentArchiveProcessor attachmentArchive,
        VerificationWorkItemProvisioner workItems,
        IOptions<KmIntegrationOptions>? kmOptions = null)
    {
        _db = db;
        _dbFactory = dbFactory;
        _moduleRecordCreation = moduleRecordCreation;
        _systemOptions = systemOptions;
        _businessWrite = businessWrite;
        _validationRequests = validationRequests;
        _stagingOptions = stagingOptions.Value;
        _attachmentArchive = attachmentArchive;
        _kmEnabled = kmOptions?.Value.Enabled == true;
        _workItems = workItems;
    }

    public async Task<VerificationApplicationDto> CreateDraftAsync(
        ClaimsPrincipal user,
        CreateVerificationApplicationRequest request,
        CancellationToken cancellationToken = default)
    {
        await _businessWrite.DemandAsync(cancellationToken);
        ArgumentNullException.ThrowIfNull(request);
        var applicant = await ResolveApplicantAsync(user, cancellationToken);
        var prepared = await PrepareDraftAsync(request, cancellationToken);

        var now = DateTime.UtcNow;
        var sequence = await _db.Database
            .SqlQueryRaw<long>("SELECT nextval('verification_application_no_seq') AS \"Value\"")
            .SingleAsync(cancellationToken);

        var entity = VerificationApplication.CreateDraft(
            Guid.NewGuid(),
            $"VA-{now:yyyyMMdd}-{sequence:D6}",
            prepared.DepartmentOptionId,
            prepared.TeamOptionId,
            NormalizeAccount(applicant.ApplicantAccount),
            Normalize(applicant.ApplicantName),
            Normalize(applicant.ApplicantEmail),
            Normalize(applicant.Department),
            Clean(applicant.ApplicantExtension),
            prepared.Content,
            now);

        SaveValidationRequest(entity, request.ValidationRequest, now);

        _db.VerificationApplications.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return await MapWithDraftOptionsAsync(entity, cancellationToken);
    }

    public async Task<VerificationApplicationDto> UpdateDraftAsync(
        Guid id,
        ClaimsPrincipal user,
        UpdateVerificationApplicationRequest request,
        CancellationToken cancellationToken = default)
    {
        await _businessWrite.DemandAsync(cancellationToken);
        ArgumentNullException.ThrowIfNull(request);
        var account = VerificationApplicationSecurity.GetAccount(user);
        var entity = await FindApplicantApplicationRequiredAsync(id, account, cancellationToken);
        var prepared = await PrepareDraftAsync(request, cancellationToken);
        var now = DateTime.UtcNow;
        entity.UpdateContent(prepared.DepartmentOptionId, prepared.TeamOptionId, prepared.Content, now);
        SaveValidationRequest(entity, request.ValidationRequest, now);
        await _db.SaveChangesAsync(cancellationToken);
        return await MapWithDraftOptionsAsync(entity, cancellationToken);
    }

    public async Task DeleteDraftAsync(
        Guid id,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        await _businessWrite.DemandAsync(cancellationToken);
        var account = VerificationApplicationSecurity.GetAccount(user);
        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
            transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        IReadOnlyList<string> exclusivePaths;
        try
        {
            var query = _db.Database.IsRelational()
                ? _db.VerificationApplications
                    .FromSqlInterpolated($"SELECT * FROM verification_applications WHERE id = {id} FOR UPDATE")
                : _db.VerificationApplications.AsQueryable();
            var entity = await query.Include(x => x.Files).SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
                ?? throw new KeyNotFoundException("找不到驗證申請單。");
            VerificationApplicationSecurity.EnsureApplicantOwnership(entity, account);
            if (entity.Status != VerificationApplicationStatus.Draft)
                throw new InvalidOperationException("只有草稿可以放棄。");

            exclusivePaths = await VerificationDraftAttachmentSafety.GetExclusivePathsAsync(
                _db, entity.Files, cancellationToken);

            _db.VerificationApplications.Remove(entity);
            await _db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            throw;
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }

        foreach (var relativePath in exclusivePaths)
        {
            var absolutePath = VerificationDraftAttachmentSafety.ResolveStagingPath(
                _stagingOptions.RootPath, relativePath);
            if (File.Exists(absolutePath)) File.Delete(absolutePath);
            VerificationDraftAttachmentSafety.DeleteEmptyApplicationDirectory(
                _stagingOptions.RootPath, absolutePath);
        }
    }

    public async Task<bool> IsDirectApprovalApplicantAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var account = VerificationApplicationSecurity.GetAccount(user);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var isActiveTeamLeader = await db.TeamRoutings.AsNoTracking().AnyAsync(x =>
            x.IsEnabled && x.LeaderAccount.ToLower() == account, cancellationToken);
        return VerificationApplicationSecurity.CanBypassReview(user, isActiveTeamLeader);
    }

    public async Task<VerificationApplicationDto> SubmitAsync(
        Guid id,
        ClaimsPrincipal user,
        DateOnly? expectedEndDate = null,
        CancellationToken cancellationToken = default)
    {
        await _businessWrite.DemandAsync(cancellationToken);
        var account = VerificationApplicationSecurity.GetAccount(user);
        var directApproval = await IsDirectApprovalApplicantAsync(user, cancellationToken);
        var directApprovalDate = ValidateDirectApprovalDate(directApproval, expectedEndDate);
        await using var transaction = await _db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        VerificationApplication entity;
        var archiveQueued = false;
        try
        {
            entity = await _db.VerificationApplications
                .FromSqlInterpolated($"SELECT * FROM verification_applications WHERE id = {id} FOR UPDATE")
                .Include(x => x.Files)
                .ThenInclude(x => x.DocumentLibraryFile)
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new KeyNotFoundException($"Verification application {id} was not found.");
            VerificationApplicationSecurity.EnsureApplicantOwnership(entity, account);
            ValidateValidationRequestForSubmit(entity);
            var now = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(entity.ValidationFormPayloadJson))
            {
                var selection = await ResolveDynamicRoutingAsync(entity.ValidationFormPayloadJson, true, cancellationToken);
                entity.UpdateDraftRouting(selection.DepartmentOptionId!.Value, selection.TeamOptionId, now);
            }
            var routing = await ResolveRoutingAsync(
                entity.RoutingDepartmentOptionId,
                entity.TeamOptionId,
                enforceDepartmentTeamRule: string.IsNullOrWhiteSpace(entity.ValidationFormPayloadJson),
                cancellationToken);
            entity.Submit(routing, now);

            if (directApproval)
            {
                var reason = user.IsInRole("Admin")
                    ? "Admin self-submission: supervisor review bypassed."
                    : "Active Team Leader self-submission: supervisor review bypassed.";
                await CreateRecordAndAcceptAsync(entity, directApprovalDate, account, now, reason, cancellationToken);
                _db.ApprovalAuditEvents.Add(ApprovalAuditEvent.Create(
                    entity.Id,
                    VerificationAuditSource,
                    VerificationApplicationWorkflow.ModuleCode,
                    ApprovalAuditActions.ApprovalBypassed,
                    account,
                    now,
                    null,
                    null,
                    details: reason));
                await _attachmentArchive.EnqueueApprovedApplicationAsync(entity, now, cancellationToken);
                archiveQueued = true;
            }

            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            if (archiveQueued) await _attachmentArchive.RemoveApplicationAsync(id);
            _db.ChangeTracker.Clear();
            throw;
        }
        if (archiveQueued)
            await _attachmentArchive.ProcessApplicationAsync(id, cancellationToken);
        return await MapWithDraftOptionsAsync(entity, cancellationToken);
    }

    public async Task<VerificationApplicationDto> UpdateForLeaderReviewAsync(
        Guid id,
        ClaimsPrincipal user,
        UpdateVerificationApplicationRequest request,
        CancellationToken cancellationToken = default)
    {
        await _businessWrite.DemandAsync(cancellationToken);
        ArgumentNullException.ThrowIfNull(request);
        var account = VerificationApplicationSecurity.GetAccount(user);
        await using var transaction = await _db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        try
        {
            var entity = await _db.VerificationApplications
                .FromSqlInterpolated($"SELECT * FROM verification_applications WHERE id = {id} FOR UPDATE")
                .Include(x => x.Files)
                .ThenInclude(x => x.DocumentLibraryFile)
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new KeyNotFoundException($"Verification application {id} was not found.");
            if (entity.Status != VerificationApplicationStatus.Submitted)
                throw new InvalidOperationException("Only Submitted applications can be edited by a reviewer.");
            VerificationApplicationSecurity.EnsureReviewerAccess(entity, user);

            var original = DeserializeValidationRequest(entity)
                ?? throw new InvalidOperationException("Legacy applications cannot be edited with the dynamic review editor.");
            var edited = request.ValidationRequest
                ?? throw new ArgumentException("The validation request is required.", nameof(request));
            if (edited.TemplateVersionId != original.TemplateVersionId || edited.TemplateVersion != original.TemplateVersion)
                throw new InvalidOperationException("The original validation form version cannot be changed during review.");

            edited.FormSnapshot = string.Equals(edited.ProductTypeCode, original.ProductTypeCode, StringComparison.OrdinalIgnoreCase)
                ? original.FormSnapshot ?? throw new InvalidOperationException("The original validation form snapshot is missing.")
                : await _validationRequests.GetPreviewAsync(original.TemplateVersionId, edited.ProductTypeCode, cancellationToken);
            ValidateSubmissionSnapshot(edited);
            var prepared = await PrepareDraftAsync(request, cancellationToken);
            var selection = await ResolveDynamicRoutingAsync(edited, true, cancellationToken);
            var routing = await ResolveRoutingAsync(
                selection.DepartmentOptionId,
                selection.TeamOptionId,
                enforceDepartmentTeamRule: false,
                cancellationToken);
            var before = AuditContent(entity.RoutingDepartmentOptionId, entity.TeamOptionId, original);
            var now = DateTime.UtcNow;
            var payloadJson = JsonSerializer.Serialize(edited, PayloadJsonOptions);
            entity.UpdateSubmittedContent(
                routing,
                prepared.Content,
                edited.TemplateVersionId,
                edited.TemplateVersion,
                edited.ProductTypeCode,
                payloadJson,
                now);
            ValidateValidationRequestForSubmit(entity);
            AddEditAudit(entity, account, now, before,
                AuditContent(routing.RoutingDepartmentOptionId, routing.TeamOptionId, edited));
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return await MapWithDraftOptionsAsync(entity, cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task<bool> CanManageAcceptedAsync(
        Guid id, ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        EnsureCsitStaff(user);
        var account = VerificationApplicationSecurity.GetAccount(user);
        return await _db.VerificationApplications.AsNoTracking().AnyAsync(x =>
            x.Id == id && x.Status == VerificationApplicationStatus.Accepted && x.ModuleRecordId != null &&
            (user.IsInRole("Admin") || x.AssignedLeaderAccount == account), cancellationToken);
    }

    public async Task<VerificationApplicationDto> UpdateAcceptedAsync(
        Guid id, ClaimsPrincipal user, UpdateVerificationApplicationRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureCsitStaff(user);
        await _businessWrite.DemandAsync(cancellationToken);
        ArgumentNullException.ThrowIfNull(request);
        var actor = VerificationApplicationSecurity.GetAccount(user);
        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            : null;
        try
        {
            var entity = await LockedAcceptedAsync(id, cancellationToken);
            VerificationApplicationSecurity.EnsureAcceptedManagerAccess(entity, user);
            var original = DeserializeValidationRequest(entity)
                ?? throw new InvalidOperationException("原申請快照缺失，無法修改已核准內容。");
            var edited = request.ValidationRequest
                ?? throw new ArgumentException("驗證申請內容不可空白。", nameof(request));
            if (edited.TemplateVersionId != original.TemplateVersionId || edited.TemplateVersion != original.TemplateVersion)
                throw new InvalidOperationException("已核准案件必須沿用原申請表單版本。");
            var productChanged = !string.Equals(
                edited.ProductTypeCode, original.ProductTypeCode, StringComparison.OrdinalIgnoreCase);
            edited.FormSnapshot = productChanged
                ? await _validationRequests.GetPreviewAsync(original.TemplateVersionId, edited.ProductTypeCode, cancellationToken)
                : original.FormSnapshot ?? throw new InvalidOperationException("原申請快照缺失，無法修改已核准內容。");
            ValidateSubmissionSnapshot(edited);
            ValidateSelectedCodes(edited);
            var prepared = await PrepareDraftAsync(request, cancellationToken);
            var selection = await ResolveDynamicRoutingAsync(edited, true, cancellationToken);
            var routing = await ResolveRoutingAsync(
                selection.DepartmentOptionId, selection.TeamOptionId, false, cancellationToken);
            var record = entity.ModuleRecord
                ?? throw new InvalidOperationException("已核准申請缺少驗證案件。");
            var tasks = record.Cases.SelectMany(x => x.Tasks).ToList();
            var selectionChanged = !original.SelectedTestItemCodes
                .ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(edited.SelectedTestItemCodes);
            var selectedCodes = edited.SelectedTestItemCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var teamChanged = entity.TeamOptionId != routing.TeamOptionId;
            var structural = productChanged || teamChanged || selectionChanged;
            var originalCodes = original.SelectedTestItemCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var taskCodes = tasks.Where(x => !string.IsNullOrWhiteSpace(x.SourceTestItemCode))
                .Select(x => x.SourceTestItemCode!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (structural && (tasks.Any(x => string.IsNullOrWhiteSpace(x.SourceTestItemCode)) ||
                               taskCodes.Count != tasks.Count ||
                               !originalCodes.SetEquals(taskCodes)))
                throw new InvalidOperationException("現有測試工作與原申請勾選項目不一致，請先確認或建立測試工作，才能修改結構。");

            var taskIds = tasks.Select(x => x.Id).ToList();
            var fileTaskIds = await _db.ModuleCaseFiles.AsNoTracking()
                .Where(x => x.TaskId != null && taskIds.Contains(x.TaskId.Value))
                .Select(x => x.TaskId!.Value).Distinct().ToListAsync(cancellationToken);
            var taskContexts = taskIds.Select(x => x.ToString("N")).ToList();
            var submittedContexts = await _db.PendingSubmissions.AsNoTracking()
                .Where(x => taskContexts.Contains(x.ContextId) &&
                    (x.ContextType == "TestReport" || x.ContextType == "TaskAttachment"))
                .Select(x => x.ContextId).Distinct().ToListAsync(cancellationToken);
            var files = fileTaskIds.ToHashSet();
            var submitted = submittedContexts.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var removed = tasks.Where(x => structural &&
                (productChanged || teamChanged || !selectedCodes.Contains(x.SourceTestItemCode!))).ToList();
            foreach (var task in tasks)
            {
                var activity = HasTaskActivity(task) || files.Contains(task.Id) || submitted.Contains(task.Id.ToString("N"));
                if (removed.Contains(task) && activity)
                    throw new InvalidOperationException($"測試項目「{task.Name}」已有執行、指派或報告紀錄，不能直接移除或改派 Team。");
                if (!removed.Contains(task) && activity &&
                    !string.Equals(ParameterSignature(original, task.SourceTestItemCode!),
                        ParameterSignature(edited, task.SourceTestItemCode!), StringComparison.Ordinal))
                    throw new InvalidOperationException($"測試項目「{task.Name}」已有執行或報告紀錄，不能改寫原測試參數。");
            }

            var before = AuditContent(entity.RoutingDepartmentOptionId, entity.TeamOptionId, original);
            var now = DateTime.UtcNow;
            entity.UpdateAcceptedContent(routing, prepared.Content,
                edited.TemplateVersionId, edited.TemplateVersion, edited.ProductTypeCode,
                JsonSerializer.Serialize(edited, PayloadJsonOptions), now);
            ValidateValidationRequestForSubmit(entity);
            SyncModuleRecord(record, entity, now);
            foreach (var task in removed)
            {
                var owner = record.Cases.Single(x => x.Id == task.CaseId);
                owner.Tasks.Remove(task);
                _db.ModuleRecordTasks.Remove(task);
            }
            if (structural)
                await _workItems.EnsureAsync(entity, record, now, cancellationToken);
            foreach (var task in tasks.Except(removed).Where(x => !HasTaskActivity(x) && !files.Contains(x.Id) &&
                !submitted.Contains(x.Id.ToString("N")) &&
                !string.Equals(ParameterSignature(original, x.SourceTestItemCode!),
                    ParameterSignature(edited, x.SourceTestItemCode!), StringComparison.Ordinal)))
                RefreshTaskSnapshot(task, edited);
            AddEditAudit(entity, actor, now, before,
                AuditContent(entity.RoutingDepartmentOptionId, entity.TeamOptionId, edited));
            await _db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return await MapWithDraftOptionsAsync(entity, cancellationToken);
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task<int> EnableSelectedWorkAsync(
        Guid id, ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        EnsureCsitStaff(user);
        await _businessWrite.DemandAsync(cancellationToken);
        var actor = VerificationApplicationSecurity.GetAccount(user);
        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            : null;
        try
        {
            var entity = await LockedAcceptedAsync(id, cancellationToken);
            VerificationApplicationSecurity.EnsureAcceptedManagerAccess(entity, user);
            var request = DeserializeValidationRequest(entity)
                ?? throw new InvalidOperationException("原申請快照缺失，不能建立測試工作。");
            ValidateSubmissionSnapshot(request);
            ValidateSelectedCodes(request);
            var record = entity.ModuleRecord
                ?? throw new InvalidOperationException("已核准申請缺少驗證案件。");
            var before = record.Cases.SelectMany(x => x.Tasks).Select(x => x.Id).ToHashSet();
            await _workItems.EnsureAsync(entity, record, DateTime.UtcNow, cancellationToken);
            var added = _db.ChangeTracker.Entries<ModuleRecordTask>()
                .Count(x => x.State == EntityState.Added && !before.Contains(x.Entity.Id));
            if (added > 0)
            {
                _db.ApprovalAuditEvents.Add(ApprovalAuditEvent.Create(
                    entity.Id, VerificationAuditSource, VerificationApplicationWorkflow.ModuleCode,
                    ApprovalAuditActions.VerificationWorkCreated, actor, DateTime.UtcNow,
                    null, null,
                    details: JsonSerializer.Serialize(new { entity.ApplicationNo, record.Id,
                        SelectedTestItemCodes = request.SelectedTestItemCodes.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                        CreatedTaskCount = added }, PayloadJsonOptions)));
            }
            await _db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return added;
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task<VerificationApplicationDto> ReturnAsync(
        Guid id,
        ClaimsPrincipal user,
        string? note,
        CancellationToken cancellationToken = default)
    {
        await _businessWrite.DemandAsync(cancellationToken);
        var account = VerificationApplicationSecurity.GetAccount(user);
        var entity = await FindLeaderApplicationRequiredAsync(id, user, cancellationToken);
        entity.Return(account, note, DateTime.UtcNow);
        await _db.SaveChangesAsync(cancellationToken);
        return await MapWithDraftOptionsAsync(entity, cancellationToken);
    }

    public async Task<VerificationApplicationDto> RejectAsync(
        Guid id,
        ClaimsPrincipal user,
        string? note,
        CancellationToken cancellationToken = default)
    {
        await _businessWrite.DemandAsync(cancellationToken);
        var account = VerificationApplicationSecurity.GetAccount(user);
        var entity = await FindLeaderApplicationRequiredAsync(id, user, cancellationToken);
        entity.Reject(account, note, DateTime.UtcNow);
        await _db.SaveChangesAsync(cancellationToken);
        return await MapWithDraftOptionsAsync(entity, cancellationToken);
    }

    public async Task<VerificationApplicationDto> AcceptAsync(
        Guid id,
        ClaimsPrincipal user,
        DateOnly expectedEndDate,
        CancellationToken cancellationToken = default)
    {
        await _businessWrite.DemandAsync(cancellationToken);
        var account = VerificationApplicationSecurity.GetAccount(user);
        VerificationApplicationSecurity.RequireDirectApprovalDate(expectedEndDate);
        await using var transaction = await _db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        VerificationApplication entity;
        var archiveQueued = false;
        try
        {
            entity = await _db.VerificationApplications
                .FromSqlInterpolated($"SELECT * FROM verification_applications WHERE id = {id} FOR UPDATE")
                .Include(x => x.Files)
                .ThenInclude(x => x.DocumentLibraryFile)
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new KeyNotFoundException($"Verification application {id} was not found.");

            if (entity.Status != VerificationApplicationStatus.Submitted)
            {
                throw new InvalidOperationException("Only Submitted applications can be accepted.");
            }

            VerificationApplicationSecurity.EnsureReviewerAccess(entity, user);

            var now = DateTime.UtcNow;
            await CreateRecordAndAcceptAsync(entity, expectedEndDate, account, now, null, cancellationToken);
            await _attachmentArchive.EnqueueApprovedApplicationAsync(entity, now, cancellationToken);
            archiveQueued = true;
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            if (archiveQueued) await _attachmentArchive.RemoveApplicationAsync(id);
            _db.ChangeTracker.Clear();
            throw;
        }
        await _attachmentArchive.ProcessApplicationAsync(id, cancellationToken);
        var accepted = await _db.VerificationApplications
            .Include(x => x.Files).ThenInclude(x => x.DocumentLibraryFile)
            .Include(x => x.ModuleRecord)
            .SingleAsync(x => x.Id == id, cancellationToken);
        return await MapWithDraftOptionsAsync(accepted, cancellationToken);
    }

    private static DateOnly ValidateDirectApprovalDate(bool directApproval, DateOnly? expectedEndDate)
    {
        if (!directApproval) return default;
        if (!expectedEndDate.HasValue || expectedEndDate.Value == default)
        {
            var issue = new VerificationApplicationValidationIssue(
                VerificationApplicationValidationIssue.DirectApprovalAreaCode,
                "主管自行送單免審",
                VerificationApplicationValidationIssue.ExpectedEndDateFieldCode,
                "預計結束日期");
            throw new VerificationApplicationValidationException(
                [issue],
                "正式送出未完成，請在『主管自行送單免審』區域填寫『預計結束日期』後重新送出。");
        }

        return VerificationApplicationSecurity.RequireDirectApprovalDate(expectedEndDate);
    }

    public async Task<VerificationApplicationDto?> GetForApplicantAsync(Guid id, ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        var account = VerificationApplicationSecurity.GetAccount(user);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.VerificationApplications
            .AsNoTracking()
            .Include(x => x.Files)
            .Include(x => x.ModuleRecord)
            .SingleOrDefaultAsync(x => x.Id == id && x.ApplicantAccount == account, cancellationToken);
        return entity is null ? null : await MapWithDraftOptionsAsync(entity, cancellationToken, db);
    }

    public async Task<VerificationApplicationDto?> GetForLeaderReviewAsync(Guid id, ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        var account = VerificationApplicationSecurity.GetAccount(user);
        var isAdmin = user.IsInRole("Admin");
        var entity = await _db.VerificationApplications.AsNoTracking().Include(x => x.Files)
            .SingleOrDefaultAsync(x => x.Id == id && x.Status == VerificationApplicationStatus.Submitted &&
                x.ApplicantAccount != account && (isAdmin || x.AssignedLeaderAccount == account), cancellationToken);
        return entity is null ? null : await MapWithDraftOptionsAsync(entity, cancellationToken);
    }

    public Task<ListResponseDto<VerificationApplicationDto>> ListForApplicantAsync(
        ClaimsPrincipal user,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var account = VerificationApplicationSecurity.GetAccount(user);
        return ListAsync(x => x.ApplicantAccount == account, page, pageSize, cancellationToken);
    }

    public Task<ListResponseDto<VerificationApplicationDto>> ListForLeaderReviewAsync(
        ClaimsPrincipal user,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var account = VerificationApplicationSecurity.GetAccount(user);
        var isAdmin = user.IsInRole("Admin");
        return ListAsync(x => x.Status == VerificationApplicationStatus.Submitted && x.ApplicantAccount != account &&
            (isAdmin || x.AssignedLeaderAccount == account), page, pageSize, cancellationToken);
    }

    public async Task<VerificationApplicationDto?> GetForCsitAsync(Guid id, ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        EnsureCsitStaff(user);
        var entity = await _db.VerificationApplications.AsNoTracking()
            .Include(x => x.Files).Include(x => x.ModuleRecord)
            .SingleOrDefaultAsync(x => x.Id == id && x.Status != VerificationApplicationStatus.Draft, cancellationToken);
        return entity is null ? null : await MapWithDraftOptionsAsync(entity, cancellationToken);
    }

    public Task<ListResponseDto<VerificationApplicationDto>> ListForCsitAsync(
        ClaimsPrincipal user, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        EnsureCsitStaff(user);
        return ListAsync(x => x.Status != VerificationApplicationStatus.Draft, page, pageSize, cancellationToken);
    }

    private async Task<ListResponseDto<VerificationApplicationDto>> ListAsync(
        System.Linq.Expressions.Expression<Func<VerificationApplication, bool>> predicate,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var query = _db.VerificationApplications.AsNoTracking().Where(predicate);

        var totalCount = await query.CountAsync(cancellationToken);
        var entities = await query
            .OrderByDescending(x => x.SubmittedAt)
            .ThenByDescending(x => x.ApplicationNo)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new ListResponseDto<VerificationApplicationDto>
        {
            Items = entities.Select(Map).ToList(),
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        };
    }

    private async Task<ApplicantSnapshot> ResolveApplicantAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var account = VerificationApplicationSecurity.GetAccount(user);
        var appUser = await _db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Account == account, cancellationToken)
            ?? throw new InvalidOperationException("Authenticated user profile was not found.");
        return new ApplicantSnapshot(appUser.Account, appUser.DisplayName, appUser.Email, appUser.Department, null);
    }

    private async Task<SystemOption> EnsureTeamAllowedAsync(
        Guid id,
        Guid? departmentOptionId,
        CancellationToken cancellationToken)
    {
        var department = await RoutingDepartmentOptions.RequireAsync(_db, departmentOptionId, cancellationToken);
        var enabledTeams = await _db.SystemOptions.AsNoTracking()
            .Where(x => x.Category == SystemOptionCategories.Team && x.IsEnabled)
            .OrderBy(x => x.Sort)
            .ThenBy(x => x.Name)
            .ToListAsync(cancellationToken);
        var allowedTeams = DepartmentTeamRules.GetAllowedTeamsForDepartment(
            department.Value,
            enabledTeams,
            team => team.Name,
            team => team.Value);
        if (allowedTeams.All(team => team.Id != id))
            throw new InvalidOperationException("The selected Team is not enabled for the selected Department.");
        return department;
    }

    private async Task<VerificationApplicationRouting> ResolveRoutingAsync(
        Guid? departmentOptionId,
        Guid? teamOptionId,
        bool enforceDepartmentTeamRule,
        CancellationToken cancellationToken)
    {
        if (!teamOptionId.HasValue) throw new InvalidOperationException("Team is required before submit.");
        var department = enforceDepartmentTeamRule
            ? await EnsureTeamAllowedAsync(teamOptionId.Value, departmentOptionId, cancellationToken)
            : await RoutingDepartmentOptions.RequireAsync(_db, departmentOptionId, cancellationToken);
        var teamLeader = await _systemOptions.GetActiveTeamLeaderAsync(teamOptionId.Value, cancellationToken);
        var moduleCode = await _db.Modules.AsNoTracking()
            .Where(x => x.Code == VerificationApplicationWorkflow.ModuleCode && x.IsEnabled)
            .Select(x => x.Code)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("The Verification Application module is missing or disabled.");

        return new VerificationApplicationRouting(
            department.Id,
            department.Value,
            DepartmentTeamRules.DisplayName(department.Name, department.Value),
            teamLeader.TeamOptionId,
            teamLeader.TeamCode,
            teamLeader.TeamName,
            moduleCode,
            teamLeader.LeaderAccount,
            teamLeader.LeaderDisplayName);
    }

    private async Task<VerificationApplication> FindApplicantApplicationRequiredAsync(Guid id, string account, CancellationToken cancellationToken)
    {
        var entity = await _db.VerificationApplications.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException($"Verification application {id} was not found.");
        VerificationApplicationSecurity.EnsureApplicantOwnership(entity, account);
        return entity;
    }

    private async Task<VerificationApplication> FindLeaderApplicationRequiredAsync(Guid id, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var entity = await _db.VerificationApplications.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException($"Verification application {id} was not found.");
        if (entity.Status != VerificationApplicationStatus.Submitted) throw new InvalidOperationException("Only Submitted applications can be reviewed.");
        VerificationApplicationSecurity.EnsureReviewerAccess(entity, user);
        return entity;
    }

    private async Task<PreparedDraft> PrepareDraftAsync(
        VerificationApplicationContentRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ValidationRequest is null)
        {
            if (request.RoutingDepartmentOptionId == Guid.Empty)
                throw new ArgumentException("RoutingDepartmentOptionId is required.", nameof(request));
            if (request.TeamOptionId == Guid.Empty)
                throw new ArgumentException("TeamOptionId is required.", nameof(request));
            await EnsureTeamAllowedAsync(request.TeamOptionId, request.RoutingDepartmentOptionId, cancellationToken);
            return new(request.RoutingDepartmentOptionId, request.TeamOptionId, MapContent(request));
        }

        var routing = await ResolveDynamicRoutingAsync(request.ValidationRequest, false, cancellationToken);
        return new(routing.DepartmentOptionId, routing.TeamOptionId, MapContent(request));
    }

    private async Task<DynamicRoutingSelection> ResolveDynamicRoutingAsync(
        string payloadJson,
        bool requireDepartment,
        CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Deserialize<ValidationRequestSubmissionDto>(payloadJson, PayloadJsonOptions)
            ?? throw new InvalidOperationException("新版驗證申請資料無效。");
        return await ResolveDynamicRoutingAsync(request, requireDepartment, cancellationToken);
    }

    private async Task<DynamicRoutingSelection> ResolveDynamicRoutingAsync(
        ValidationRequestSubmissionDto request,
        bool requireDepartment,
        CancellationToken cancellationToken)
    {
        var location = FieldValue(request, "LOCATION");
        if (!string.Equals(location, "TAIPEI", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("New validation applications currently support Taipei (TAIPEI) only.");

        var departmentCode = FieldValue(request, "DEPARTMENT_CODE");
        if (!string.IsNullOrWhiteSpace(departmentCode))
        {
            var externalDepartmentExists = (await _db.SystemOptions.AsNoTracking()
                    .Where(x => x.Category == SystemOptionCategories.Department && x.IsEnabled)
                    .ToListAsync(cancellationToken))
                .Any(x => string.Equals(x.Value.Trim(), departmentCode.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!externalDepartmentExists)
                throw new InvalidOperationException("Department Code 不是目前可用的 Department 選項。");
        }
        else if (requireDepartment)
        {
            throw new InvalidOperationException("請選擇 Department Code。");
        }

        var routingDepartment = await ResolveInternalRoutingDepartmentAsync("TAIPEI", cancellationToken);

        var product = await _db.ValidationProductTypes.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Code == request.ProductTypeCode && x.IsEnabled, cancellationToken)
            ?? throw new InvalidOperationException("所選 Product Type 已停用或不存在。");
        if (!product.TeamOptionId.HasValue)
            throw new InvalidOperationException($"Product Type「{product.DisplayLabel}」尚未設定對應 Team。");

        var team = await _db.SystemOptions.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == product.TeamOptionId.Value && x.Category == SystemOptionCategories.Team && x.IsEnabled,
            cancellationToken);
        if (team is null)
            throw new InvalidOperationException($"Product Type「{product.DisplayLabel}」對應的 Team 已停用或不存在。");

        return new(routingDepartment.Id, team.Id);
    }

    private async Task<SystemOption> ResolveInternalRoutingDepartmentAsync(
        string location,
        CancellationToken cancellationToken)
    {
        var candidates = (await _db.SystemOptions.AsNoTracking()
                .Where(x => x.Category == SystemOptionCategories.Department && x.IsEnabled)
                .ToListAsync(cancellationToken))
            .Where(x => DepartmentFamilyMatcher.MatchesLocation(x.Value, location))
            .ToList();

        if (candidates.Count == 0)
            throw new InvalidOperationException("No enabled internal routing Department is configured for Taipei.");
        if (candidates.Count > 1)
            throw new InvalidOperationException("Multiple enabled internal routing Departments are configured for Taipei; routing is ambiguous.");
        return candidates[0];
    }

    private static VerificationApplicationContent MapContent(VerificationApplicationContentRequest request)
    {
        var dynamic = request.ValidationRequest;
        if (dynamic is null)
        {
            return new VerificationApplicationContent
            {
                ProjectName = request.ProjectName,
                SubPu = request.SubPu,
                Customer = request.Customer,
                ProductModel = request.ProductModel,
                RequestedFinishDate = request.RequestedFinishDate,
                ValidationRequirement = request.ValidationRequirement,
                HardwareVersion = request.HardwareVersion,
                FirmwareVersion = request.FirmwareVersion,
                SoftwareVersion = request.SoftwareVersion,
                SampleReadyDate = request.SampleReadyDate,
                JiraLink = request.JiraLink,
                Location = request.Location,
                Npi = request.Npi,
                WirelessDrive = request.WirelessDrive,
                Chipset = request.Chipset,
                SampleMacAddress = request.SampleMacAddress,
                UtilityVersion = request.UtilityVersion,
                DspModel = request.DspModel
            };
        }

        var modelName = FieldValue(dynamic, "MODEL_NAME");
        return new VerificationApplicationContent
        {
            ProjectName = modelName ?? string.Empty,
            Customer = FieldValue(dynamic, "CUSTOMER"),
            ProductModel = modelName,
            RequestedFinishDate = null,
            ValidationRequirement = BuildValidationRequirement(dynamic),
            SubPu = FieldValue(dynamic, "PU_CODE"),
            Location = FieldValue(dynamic, "LOCATION"),
            Npi = FieldValue(dynamic, "NPI_PHASE"),
            HardwareVersion = FieldValue(dynamic, "HW_VERSION"),
            FirmwareVersion = FieldValue(dynamic, "FW_SW_VERSION"),
            SoftwareVersion = FieldValue(dynamic, "FW_SW_VERSION"),
            WirelessDrive = FieldValue(dynamic, "WIFI_DRIVER_VERSION"),
            Chipset = FieldValue(dynamic, "CPU_MODEL")
        };
    }

    private static string? BuildValidationRequirement(ValidationRequestSubmissionDto request)
    {
        var snapshot = request.FormSnapshot;
        if (snapshot is null || request.SelectedTestItemCodes.Count == 0) return null;
        var selected = snapshot.TestCategories.SelectMany(x => x.TestItems)
            .Where(x => request.SelectedTestItemCodes.Contains(x.Code))
            .Select(item =>
            {
                if (!request.TestParameterValues.TryGetValue(item.Code, out var values)) return item.DisplayLabel;
                var definitions = item.Parameters.SelectMany(FlattenParameters)
                    .ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);
                var parameters = values.Where(x => x.Value.Any(v => !string.IsNullOrWhiteSpace(v)))
                    .Select(x =>
                    {
                        definitions.TryGetValue(x.Key, out var definition);
                        var display = definition?.FieldType == "SensitiveText"
                            ? "[REDACTED]"
                            : string.Join(", ", x.Value.Where(v => !string.IsNullOrWhiteSpace(v)));
                        return $"{definition?.DisplayLabel ?? x.Key}={display}";
                    })
                    .ToList();
                return parameters.Count == 0 ? item.DisplayLabel : $"{item.DisplayLabel} ({string.Join("; ", parameters)})";
            })
            .ToList();
        return selected.Count == 0 ? null : string.Join("; ", selected);
    }

    private static string? FieldValue(ValidationRequestSubmissionDto? request, string code) =>
        request?.FieldValues.TryGetValue(code, out var values) == true
            ? values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim()
            : null;

    private sealed record PreparedDraft(Guid? DepartmentOptionId, Guid? TeamOptionId, VerificationApplicationContent Content);
    private sealed record DynamicRoutingSelection(Guid? DepartmentOptionId, Guid TeamOptionId);

    private static void SaveValidationRequest(VerificationApplication entity, ValidationRequestSubmissionDto? request, DateTime now)
    {
        if (request is null) return;
        ValidateSubmissionSnapshot(request);
        entity.SetValidationRequest(request.TemplateVersionId, request.TemplateVersion, request.ProductTypeCode,
            JsonSerializer.Serialize(request, PayloadJsonOptions), now);
    }

    private static void ValidateValidationRequestForSubmit(VerificationApplication entity)
    {
        if (string.IsNullOrWhiteSpace(entity.ValidationFormPayloadJson)) return;
        var request = JsonSerializer.Deserialize<ValidationRequestSubmissionDto>(entity.ValidationFormPayloadJson, PayloadJsonOptions)
            ?? throw new InvalidOperationException("The validation form data is invalid.");
        var snapshot = request.FormSnapshot ?? throw new InvalidOperationException("The validation form snapshot is missing.");
        var issues = snapshot.Sections.SelectMany(section => section.Fields.SelectMany(Flatten)
                .Where(field => field.IsEnabled && IsVisible(field, request.FieldValues) && IsRequired(field, request.FieldValues))
                .Where(field => field.FieldType is not ("Attachment" or "Group" or "RepeatableGroup"))
                .Where(field => !request.FieldValues.TryGetValue(field.Code, out var values) || values.All(string.IsNullOrWhiteSpace))
                .Select(field => new VerificationApplicationValidationIssue(
                    section.Code, section.DisplayLabel, field.Code, field.DisplayLabel)))
            .Concat(snapshot.TestCategories.SelectMany(category => category.TestItems)
                .Where(item => item.IsEnabled && item.IsMandatory && !request.SelectedTestItemCodes.Contains(item.Code))
                .Select(item => new VerificationApplicationValidationIssue(
                    VerificationApplicationValidationIssue.TestItemsAreaCode, "測試項目", null, item.DisplayLabel)))
            .Concat(snapshot.TestCategories.SelectMany(category => category.TestItems)
                .Where(item => item.IsEnabled && request.SelectedTestItemCodes.Contains(item.Code))
                .SelectMany(item => item.Parameters.SelectMany(FlattenParameters)
                    .Where(parameter => parameter.IsEnabled && parameter.IsRequired)
                    .Where(parameter => !request.TestParameterValues.TryGetValue(item.Code, out var values) ||
                                        !values.TryGetValue(parameter.Code, out var answers) || answers.All(string.IsNullOrWhiteSpace))
                    .Select(parameter => new VerificationApplicationValidationIssue(
                        VerificationApplicationValidationIssue.TestItemsAreaCode, "測試項目", null,
                        $"{item.DisplayLabel}／{parameter.DisplayLabel}"))))
            .ToList();
        if (issues.Count > 0) throw new VerificationApplicationValidationException(issues);
    }

    private static IEnumerable<ValidationFieldDto> Flatten(ValidationFieldDto field)
    {
        yield return field;
        foreach (var child in field.Children.SelectMany(Flatten)) yield return child;
    }

    private static IEnumerable<ValidationParameterDto> FlattenParameters(ValidationParameterDto parameter)
    {
        yield return parameter;
        foreach (var child in parameter.Children.SelectMany(FlattenParameters)) yield return child;
    }

    private static bool IsVisible(ValidationFieldDto field, IReadOnlyDictionary<string, List<string>> values) =>
        field.Conditions.Where(x => x.Action == "Visible").All(x => Evaluate(x, values));

    private static bool IsRequired(ValidationFieldDto field, IReadOnlyDictionary<string, List<string>> values) =>
        field.IsRequired || field.Conditions.Where(x => x.Action == "Required").Any(x => Evaluate(x, values));

    private static bool Evaluate(ValidationConditionDto condition, IReadOnlyDictionary<string, List<string>> values)
    {
        values.TryGetValue(condition.SourceFieldCode, out var actual);
        actual ??= [];
        var expected = condition.ExpectedValue ?? string.Empty;
        return condition.Operator switch
        {
            "Equals" => actual.Any(x => string.Equals(x, expected, StringComparison.OrdinalIgnoreCase)),
            "NotEquals" => actual.All(x => !string.Equals(x, expected, StringComparison.OrdinalIgnoreCase)),
            "Contains" => actual.Contains(expected, StringComparer.OrdinalIgnoreCase),
            "NotContains" => !actual.Contains(expected, StringComparer.OrdinalIgnoreCase),
            "IsTrue" => actual.Any(x => string.Equals(x, "true", StringComparison.OrdinalIgnoreCase)),
            "IsFalse" => actual.All(x => !string.Equals(x, "true", StringComparison.OrdinalIgnoreCase)),
            _ => false
        };
    }

    private async Task<VerificationApplication> LockedAcceptedAsync(Guid id, CancellationToken cancellationToken)
    {
        var query = _db.Database.IsRelational()
            ? _db.VerificationApplications
                .FromSqlInterpolated($"SELECT * FROM verification_applications WHERE id = {id} FOR UPDATE")
            : _db.VerificationApplications.AsQueryable();
        var entity = await query.Include(x => x.ModuleRecord!).ThenInclude(x => x.Cases)
            .ThenInclude(x => x.Tasks).SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("找不到驗證申請單。");
        if (entity.Status != VerificationApplicationStatus.Accepted || entity.ModuleRecord is null)
            throw new InvalidOperationException("只有已核准且建立案件的申請可以管理。");
        return entity;
    }

    private static void ValidateSelectedCodes(ValidationRequestSubmissionDto request)
    {
        if (request.FieldValues is null || request.TestParameterValues is null ||
            request.SelectedTestItemCodes is null || request.FormSnapshot?.Sections is null ||
            request.FormSnapshot.TestCategories is null)
            throw new InvalidOperationException("申請資料或原表單快照不完整。");
        var fields = request.FormSnapshot.Sections.SelectMany(x => x.Fields).SelectMany(Flatten)
            .Select(x => x.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (request.FieldValues.Keys.Any(x => !fields.Contains(x)))
            throw new InvalidOperationException("申請資料包含原表單未定義的欄位。");
        var selected = request.SelectedTestItemCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = request.FormSnapshot.TestCategories.SelectMany(x => x.TestItems)
            .Where(x => selected.Contains(x.Code)).Select(x => x.Code).ToList();
        if (selected.Count == 0 || found.Count != selected.Count ||
            found.Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Count)
            throw new InvalidOperationException("原申請勾選項目與表單快照不相容，不能修改或建立測試工作。");
        var parameterCodes = request.FormSnapshot.TestCategories.SelectMany(x => x.TestItems)
            .ToDictionary(x => x.Code,
                x => x.Parameters.SelectMany(FlattenParameters).Select(p => p.Code)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        if (request.TestParameterValues.Any(x => !parameterCodes.TryGetValue(x.Key, out var codes) ||
            x.Value.Keys.Any(code => !codes.Contains(code))))
            throw new InvalidOperationException("申請資料包含原表單未定義的測試參數。");
    }

    private static bool HasTaskActivity(ModuleRecordTask task) =>
        !string.Equals(task.Status, "Open", StringComparison.OrdinalIgnoreCase) ||
        !string.IsNullOrWhiteSpace(task.AssignEngineer) || task.ActualCompletionDate.HasValue ||
        task.StartDate.HasValue || !string.IsNullOrWhiteSpace(task.Result) ||
        !string.IsNullOrWhiteSpace(task.Quoted) || !string.IsNullOrWhiteSpace(task.Reimburse) ||
        task.UpdatedAt > task.CreatedAt;

    private static string ParameterSignature(ValidationRequestSubmissionDto request, string code)
    {
        var values = request.TestParameterValues.FirstOrDefault(x =>
            string.Equals(x.Key, code, StringComparison.OrdinalIgnoreCase)).Value;
        return values is null ? string.Empty : JsonSerializer.Serialize(values
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => new { Code = x.Key.ToUpperInvariant(), Values = x.Value }), PayloadJsonOptions);
    }

    private static void RefreshTaskSnapshot(ModuleRecordTask task, ValidationRequestSubmissionDto request)
    {
        var item = request.FormSnapshot!.TestCategories.SelectMany(x => x.TestItems)
            .Single(x => string.Equals(x.Code, task.SourceTestItemCode, StringComparison.OrdinalIgnoreCase));
        var values = request.TestParameterValues.FirstOrDefault(x =>
            string.Equals(x.Key, item.Code, StringComparison.OrdinalIgnoreCase)).Value;
        task.SourceTestParametersJson = JsonSerializer.Serialize(
            new { definitions = item.Parameters, values }, PayloadJsonOptions);
    }

    private static void SyncModuleRecord(ModuleRecord record, VerificationApplication entity, DateTime now)
    {
        record.Name = entity.ProjectName;
        record.Customer = entity.Customer;
        record.Team = entity.TeamCode;
        record.Department = entity.RoutingDepartmentCode;
        record.RequestDepartment = entity.Department;
        record.RequestApplicant = entity.ApplicantName;
        record.CustomerProductName = entity.ProductModel;
        record.SampleReadyDate = entity.SampleReadyDate;
        record.ApplicantNote = entity.ValidationRequirement;
        record.Npi = entity.Npi;
        record.HardwareVersion = entity.HardwareVersion;
        record.SoftwareVersion = entity.SoftwareVersion;
        record.Location = entity.Location;
        record.SubPu = entity.SubPu;
        record.FirmwareVersion = entity.FirmwareVersion;
        record.WirelessDrive = entity.WirelessDrive;
        record.Chipset = entity.Chipset;
        record.SampleMacAddress = entity.SampleMacAddress;
        record.UtilityVersion = entity.UtilityVersion;
        record.DspModel = entity.DspModel;
        record.JiraLink = entity.JiraLink;
        record.UpdatedAt = now;
    }

    private async Task CreateRecordAndAcceptAsync(
        VerificationApplication entity,
        DateOnly expectedEndDate,
        string processedBy,
        DateTime now,
        string? processingNote,
        CancellationToken cancellationToken)
    {
        if (entity.ModuleRecordId.HasValue)
            throw new InvalidOperationException("The application already has a ModuleRecord.");
        var moduleCode = string.IsNullOrWhiteSpace(entity.ModuleCode)
            ? throw new InvalidOperationException("The submitted application has no resolved ModuleCode.")
            : entity.ModuleCode;
        var moduleRecord = await _moduleRecordCreation.CreateAsync(new ModuleRecordCreationRequest
        {
            ModuleCode = moduleCode,
            Name = entity.ProjectName,
            Customer = entity.Customer,
            Status = "Open",
            Progress = 0,
            ExpectedEndDate = expectedEndDate,
            SampleReadyDate = entity.SampleReadyDate,
            ApplicantNote = entity.ValidationRequirement,
            Npi = entity.Npi,
            HardwareVersion = entity.HardwareVersion,
            SoftwareVersion = entity.SoftwareVersion,
            Location = entity.Location,
            Department = entity.RoutingDepartmentCode,
            Team = entity.TeamCode,
            RequestDepartment = entity.Department,
            RequestApplicant = entity.ApplicantName,
            SubPu = entity.SubPu,
            FirmwareVersion = entity.FirmwareVersion,
            WirelessDrive = entity.WirelessDrive,
            CustomerProductName = entity.ProductModel,
            Chipset = entity.Chipset,
            SampleMacAddress = entity.SampleMacAddress,
            UtilityVersion = entity.UtilityVersion,
            DspModel = entity.DspModel,
            JiraLink = entity.JiraLink
        }, cancellationToken);
        await _workItems.EnsureAsync(entity, moduleRecord, now, cancellationToken);
        entity.Accept(moduleRecord.Id, expectedEndDate, processedBy, now, processingNote);
    }

    private static ValidationRequestSubmissionDto? DeserializeValidationRequest(VerificationApplication entity) =>
        string.IsNullOrWhiteSpace(entity.ValidationFormPayloadJson)
            ? null
            : JsonSerializer.Deserialize<ValidationRequestSubmissionDto>(entity.ValidationFormPayloadJson, PayloadJsonOptions);

    private static void ValidateSubmissionSnapshot(ValidationRequestSubmissionDto request)
    {
        if (request.FormSnapshot is null)
            throw new ArgumentException("The validation form snapshot is required.");
        if (request.TemplateVersionId != request.FormSnapshot.TemplateVersionId ||
            request.TemplateVersion != request.FormSnapshot.TemplateVersion)
            throw new ArgumentException("The validation form version does not match its snapshot.");
        if (!string.Equals(request.ProductTypeCode, request.FormSnapshot.ProductTypeCode, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The validation product type does not match its snapshot.");
    }

    private static object AuditContent(
        Guid? departmentOptionId,
        Guid? teamOptionId,
        ValidationRequestSubmissionDto request)
    {
        var sensitiveFieldCodes = request.FormSnapshot?.Sections
            .SelectMany(x => x.Fields)
            .SelectMany(Flatten)
            .Where(x => x.IsSensitive || string.Equals(x.FieldType, "SensitiveText", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var sensitiveParameters = request.FormSnapshot?.TestCategories
            .SelectMany(x => x.TestItems)
            .ToDictionary(
                x => x.Code,
                x => x.Parameters.SelectMany(FlattenParameters)
                    .Where(parameter => string.Equals(parameter.FieldType, "SensitiveText", StringComparison.OrdinalIgnoreCase))
                    .Select(parameter => parameter.Code)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        var fieldValues = request.FieldValues.ToDictionary(
            x => x.Key,
            x => RedactSensitiveValues(x.Value, sensitiveFieldCodes.Contains(x.Key)),
            StringComparer.OrdinalIgnoreCase);
        var testParameterValues = request.TestParameterValues.ToDictionary(
            test => test.Key,
            test => test.Value.ToDictionary(
                parameter => parameter.Key,
                parameter => RedactSensitiveValues(
                    parameter.Value,
                    sensitiveParameters.TryGetValue(test.Key, out var codes) && codes.Contains(parameter.Key)),
                StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        return new
        {
            DepartmentOptionId = departmentOptionId,
            TeamOptionId = teamOptionId,
            request.TemplateVersionId,
            request.TemplateVersion,
            request.ProductTypeCode,
            FieldValues = fieldValues,
            SelectedTestItemCodes = request.SelectedTestItemCodes.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            TestParameterValues = testParameterValues
        };
    }

    private static List<string> RedactSensitiveValues(IReadOnlyList<string> values, bool sensitive) =>
        sensitive
            ? values.Select(value => string.IsNullOrWhiteSpace(value) ? string.Empty : "[REDACTED]").ToList()
            : values.ToList();

    private void AddEditAudit(
        VerificationApplication entity,
        string actor,
        DateTime now,
        object before,
        object after)
    {
        var details = JsonSerializer.Serialize(new { entity.ApplicationNo, Before = before, After = after }, PayloadJsonOptions);
        var chunkCount = Math.Max(1, (details.Length + AuditDetailsChunkSize - 1) / AuditDetailsChunkSize);
        for (var index = 0; index < chunkCount; index++)
        {
            var offset = index * AuditDetailsChunkSize;
            var length = Math.Min(AuditDetailsChunkSize, details.Length - offset);
            var chunk = $"part {index + 1}/{chunkCount}:" + details.Substring(offset, length);
            _db.ApprovalAuditEvents.Add(ApprovalAuditEvent.Create(
                entity.Id,
                VerificationAuditSource,
                VerificationApplicationWorkflow.ModuleCode,
                ApprovalAuditActions.VerificationContentEdited,
                actor,
                now,
                null,
                null,
                details: chunk));
        }
    }

    private static void EnsureCsitStaff(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true ||
            !user.HasClaim(SystemAuthorization.AccessScopeClaim, SystemAuthorization.AccessScopes.CsitStaff))
            throw new UnauthorizedAccessException("CSIT staff access is required.");
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;
    private static string NormalizeAccount(string value) => value.Trim().ToLowerInvariant();

    private async Task<VerificationApplicationDto> MapWithDraftOptionsAsync(
        VerificationApplication entity,
        CancellationToken cancellationToken,
        AppDbContext? db = null)
    {
        var dto = Map(entity);
        await PopulateFormalArchiveStateAsync(dto, entity.Status, db ?? _db, cancellationToken);
        if (entity.Status is VerificationApplicationStatus.Draft or VerificationApplicationStatus.Returned)
        {
            var options = await (db ?? _db).SystemOptions.AsNoTracking()
                .Where(x => x.Id == entity.RoutingDepartmentOptionId || x.Id == entity.TeamOptionId)
                .ToListAsync(cancellationToken);
            var department = options.FirstOrDefault(x => x.Id == entity.RoutingDepartmentOptionId);
            var team = options.FirstOrDefault(x => x.Id == entity.TeamOptionId);
            dto.RoutingDepartmentCode = department?.Value;
            dto.RoutingDepartmentName = department is null ? null : DepartmentTeamRules.DisplayName(department.Name, department.Value);
            dto.TeamCode = team?.Value;
            dto.TeamName = team is null ? null : DepartmentTeamRules.DisplayName(team.Name, team.Value);
        }
        return dto;
    }

    private async Task PopulateFormalArchiveStateAsync(
        VerificationApplicationDto dto,
        VerificationApplicationStatus applicationStatus,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (dto.Files.Count == 0) return;
        var ids = dto.Files.Select(x => x.Id).ToList();
        var records = await db.VerificationApplicationFiles.AsNoTracking()
            .Where(x => ids.Contains(x.Id))
            .Select(x => new
            {
                x.Id,
                x.DocumentLibraryFileId,
                FolderId = x.DocumentLibraryFile == null ? (Guid?)null : x.DocumentLibraryFile.FolderId,
                StorageArea = x.DocumentLibraryFile == null ? null : x.DocumentLibraryFile.StorageArea
            })
            .ToListAsync(cancellationToken);
        var byId = records.ToDictionary(x => x.Id);
        var kmRows = await db.KmIntegrationOutbox.AsNoTracking()
            .Where(x => x.EventType == KmIntegrationEventTypes.VerificationApplicationAttachmentArchived &&
                        x.SourceEntityType == KmIntegrationSourceEntityTypes.VerificationApplicationFile &&
                        ids.Contains(x.SourceEntityId))
            .ToDictionaryAsync(x => x.SourceEntityId, cancellationToken);
        var pending = (await _attachmentArchive.ReadPendingAsync(cancellationToken))
            .Where(x => ids.Contains(x.AttachmentId))
            .ToDictionary(x => x.AttachmentId);
        foreach (var file in dto.Files)
        {
            if (byId.TryGetValue(file.Id, out var record))
            {
                file.DocumentLibraryFileId = record.DocumentLibraryFileId;
                file.DocumentFolderId = record.FolderId;
            }
            if (applicationStatus != VerificationApplicationStatus.Accepted)
            {
                file.FormalArchiveStatus = VerificationAttachmentArchiveStatuses.NotApplicable;
                VerificationAttachmentKmState.Apply(file, false, null, _kmEnabled);
                continue;
            }
            if (byId.TryGetValue(file.Id, out record) &&
                record.StorageArea == DocumentStorageAreas.Library)
            {
                file.FormalArchiveStatus = VerificationAttachmentArchiveStatuses.Completed;
                VerificationAttachmentKmState.Apply(
                    file, true, kmRows.GetValueOrDefault(file.Id), _kmEnabled);
                continue;
            }
            if (pending.TryGetValue(file.Id, out var entry) && !string.IsNullOrWhiteSpace(entry.LastError))
            {
                file.FormalArchiveStatus = VerificationAttachmentArchiveStatuses.Failed;
                file.FormalArchiveError = entry.LastError;
            }
            else
            {
                file.FormalArchiveStatus = VerificationAttachmentArchiveStatuses.Processing;
            }
            VerificationAttachmentKmState.Apply(file, false, null, _kmEnabled);
        }
    }

    private static VerificationApplicationDto Map(VerificationApplication entity) => new()
    {
        Id = entity.Id,
        ApplicationNo = entity.ApplicationNo,
        RoutingDepartmentOptionId = entity.RoutingDepartmentOptionId,
        RoutingDepartmentCode = entity.RoutingDepartmentCode,
        RoutingDepartmentName = entity.RoutingDepartmentName,
        TeamOptionId = entity.TeamOptionId,
        TeamCode = entity.TeamCode ?? entity.CategoryCode,
        TeamName = entity.TeamName ?? entity.CategoryName,
        ApplicantAccount = entity.ApplicantAccount,
        ApplicantName = entity.ApplicantName,
        ApplicantEmail = entity.ApplicantEmail,
        Department = entity.Department,
        ApplicantExtension = entity.ApplicantExtension,
        ProjectName = entity.ProjectName,
        SubPu = entity.SubPu,
        Customer = entity.Customer,
        ProductModel = entity.ProductModel,
        RequestedFinishDate = entity.RequestedFinishDate,
        ExpectedEndDate = entity.ExpectedEndDate,
        ValidationRequirement = entity.ValidationRequirement,
        HardwareVersion = entity.HardwareVersion,
        FirmwareVersion = entity.FirmwareVersion,
        SoftwareVersion = entity.SoftwareVersion,
        SampleReadyDate = entity.SampleReadyDate,
        JiraLink = entity.JiraLink,
        Location = entity.Location,
        Npi = entity.Npi,
        WirelessDrive = entity.WirelessDrive,
        Chipset = entity.Chipset,
        SampleMacAddress = entity.SampleMacAddress,
        UtilityVersion = entity.UtilityVersion,
        DspModel = entity.DspModel,
        Status = entity.Status,
        SubmittedAt = entity.SubmittedAt,
        ReturnedAt = entity.ReturnedAt,
        RejectedAt = entity.RejectedAt,
        AcceptedAt = entity.AcceptedAt,
        ProcessedAt = entity.ProcessedAt,
        ProcessedBy = entity.ProcessedBy,
        ProcessingNote = entity.ProcessingNote,
        ModuleRecordNo = entity.Status == VerificationApplicationStatus.Accepted ? entity.ModuleRecord?.RecordNo : null,
        UpdatedAt = entity.UpdatedAt,
        ValidationRequest = string.IsNullOrWhiteSpace(entity.ValidationFormPayloadJson)
            ? null
            : JsonSerializer.Deserialize<ValidationRequestSubmissionDto>(entity.ValidationFormPayloadJson, PayloadJsonOptions),
        Files = entity.Files.Select(x => new VerificationApplicationFileDto
        {
            Id = x.Id,
            FileName = x.FileName,
            ContentType = x.ContentType,
            FileSize = x.FileSize,
            UploadedBy = x.UploadedBy,
            UploadedAt = x.CreatedAt,
            ModelName = entity.ProductModel,
            DocumentFolderId = x.DocumentLibraryFile?.FolderId,
            DocumentLibraryFileId = x.DocumentLibraryFileId
        }).ToList()
    };
}
