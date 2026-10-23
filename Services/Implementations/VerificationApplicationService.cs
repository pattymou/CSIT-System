using System.Data;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Services.Implementations;

public sealed class VerificationApplicationService : IVerificationApplicationService
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AppDbContext _db;
    private readonly IModuleRecordCreationService _moduleRecordCreation;
    private readonly ISystemOptionService _systemOptions;
    private readonly IBusinessWriteAuthorizationGuard _businessWrite;

    public VerificationApplicationService(
        AppDbContext db,
        IModuleRecordCreationService moduleRecordCreation,
        ISystemOptionService systemOptions,
        IBusinessWriteAuthorizationGuard businessWrite)
    {
        _db = db;
        _moduleRecordCreation = moduleRecordCreation;
        _systemOptions = systemOptions;
        _businessWrite = businessWrite;
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

    public async Task<VerificationApplicationDto> SubmitAsync(Guid id, ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        await _businessWrite.DemandAsync(cancellationToken);
        var account = VerificationApplicationSecurity.GetAccount(user);
        var entity = await FindApplicantApplicationRequiredAsync(id, account, cancellationToken);
        ValidateValidationRequestForSubmit(entity);
        if (!string.IsNullOrWhiteSpace(entity.ValidationFormPayloadJson))
        {
            var selection = await ResolveDynamicRoutingAsync(entity.ValidationFormPayloadJson, true, cancellationToken);
            entity.UpdateDraftRouting(selection.DepartmentOptionId!.Value, selection.TeamOptionId, DateTime.UtcNow);
        }
        var routing = await ResolveRoutingAsync(entity.RoutingDepartmentOptionId, entity.TeamOptionId, cancellationToken);
        entity.Submit(routing, DateTime.UtcNow);
        await _db.SaveChangesAsync(cancellationToken);
        return await MapWithDraftOptionsAsync(entity, cancellationToken);
    }

    public async Task<VerificationApplicationDto> ReturnAsync(
        Guid id,
        ClaimsPrincipal user,
        string? note,
        CancellationToken cancellationToken = default)
    {
        await _businessWrite.DemandAsync(cancellationToken);
        var account = VerificationApplicationSecurity.GetAccount(user);
        var entity = await FindLeaderApplicationRequiredAsync(id, account, cancellationToken);
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
        var entity = await FindLeaderApplicationRequiredAsync(id, account, cancellationToken);
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
        await using var transaction = await _db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        try
        {
            var entity = await _db.VerificationApplications
                .FromSqlInterpolated($"SELECT * FROM verification_applications WHERE id = {id} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new KeyNotFoundException($"Verification application {id} was not found.");

            if (entity.Status != VerificationApplicationStatus.Submitted)
            {
                throw new InvalidOperationException("Only Submitted applications can be accepted.");
            }

            VerificationApplicationSecurity.EnsureLeaderOwnership(entity, account);

            if (entity.ModuleRecordId.HasValue)
            {
                throw new InvalidOperationException("The application already has a ModuleRecord.");
            }

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

            entity.Accept(moduleRecord.Id, expectedEndDate, account, DateTime.UtcNow);
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

    public async Task<VerificationApplicationDto?> GetForApplicantAsync(Guid id, ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        var account = VerificationApplicationSecurity.GetAccount(user);
        var entity = await _db.VerificationApplications
            .AsNoTracking()
            .Include(x => x.Files)
            .Include(x => x.ModuleRecord)
            .SingleOrDefaultAsync(x => x.Id == id && x.ApplicantAccount == account, cancellationToken);
        return entity is null ? null : await MapWithDraftOptionsAsync(entity, cancellationToken);
    }

    public async Task<VerificationApplicationDto?> GetForLeaderReviewAsync(Guid id, ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        var account = VerificationApplicationSecurity.GetAccount(user);
        var entity = await _db.VerificationApplications.AsNoTracking().Include(x => x.Files)
            .SingleOrDefaultAsync(x => x.Id == id && x.Status == VerificationApplicationStatus.Submitted && x.AssignedLeaderAccount == account, cancellationToken);
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
        return ListAsync(x => x.Status == VerificationApplicationStatus.Submitted && x.AssignedLeaderAccount == account, page, pageSize, cancellationToken);
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
        var enabledTeams = await _systemOptions.GetEnabledByCategoryAsync(SystemOptionCategories.Team);
        var allowedTeams = DepartmentTeamRules.GetAllowedTeamsForDepartment(
            department.Value,
            enabledTeams,
            team => team.Name,
            team => team.Value);
        if (allowedTeams.All(team => team.Id != id))
            throw new InvalidOperationException("The selected Team is not enabled for the selected Department.");
        return department;
    }

    private async Task<VerificationApplicationRouting> ResolveRoutingAsync(Guid? departmentOptionId, Guid? teamOptionId, CancellationToken cancellationToken)
    {
        if (!teamOptionId.HasValue) throw new InvalidOperationException("Team is required before submit.");
        var department = await EnsureTeamAllowedAsync(teamOptionId.Value, departmentOptionId, cancellationToken);
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

    private async Task<VerificationApplication> FindLeaderApplicationRequiredAsync(Guid id, string account, CancellationToken cancellationToken)
    {
        var entity = await _db.VerificationApplications.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException($"Verification application {id} was not found.");
        if (entity.Status != VerificationApplicationStatus.Submitted) throw new InvalidOperationException("Only Submitted applications can be reviewed.");
        VerificationApplicationSecurity.EnsureLeaderOwnership(entity, account);
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
        var departmentCode = FieldValue(request, "DEPARTMENT_CODE");
        SystemOption? department = null;
        if (!string.IsNullOrWhiteSpace(departmentCode))
        {
            department = (await _db.SystemOptions.AsNoTracking()
                    .Where(x => x.Category == SystemOptionCategories.Department && x.IsEnabled)
                    .ToListAsync(cancellationToken))
                .SingleOrDefault(x => DepartmentFamilyMatcher.IsDa40Family(x.Value) &&
                                      string.Equals(x.Value.Trim(), departmentCode.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Department Code 不是目前可用的 Department 選項。");
        }
        else if (requireDepartment)
        {
            throw new InvalidOperationException("請選擇 Department Code。");
        }

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

        if (department is not null)
        {
            var enabledTeams = await _systemOptions.GetEnabledByCategoryAsync(SystemOptionCategories.Team);
            var allowedTeams = DepartmentTeamRules.GetAllowedTeamsForDepartment(
                department.Value, enabledTeams, x => x.Name, x => x.Value);
            if (allowedTeams.All(x => x.Id != team.Id))
                throw new InvalidOperationException($"Team「{DepartmentTeamRules.DisplayName(team.Name, team.Value)}」不適用於所選 Department。");
        }

        return new(department?.Id, team.Id);
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
                var labels = item.Parameters.SelectMany(FlattenParameters).ToDictionary(x => x.Code, x => x.DisplayLabel, StringComparer.OrdinalIgnoreCase);
                var parameters = values.Where(x => x.Value.Any(v => !string.IsNullOrWhiteSpace(v)))
                    .Select(x => $"{labels.GetValueOrDefault(x.Key, x.Key)}={string.Join(", ", x.Value.Where(v => !string.IsNullOrWhiteSpace(v)))}")
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
        if (request.FormSnapshot is null) throw new ArgumentException("The validation form snapshot is required.");
        if (request.TemplateVersionId != request.FormSnapshot.TemplateVersionId || request.TemplateVersion != request.FormSnapshot.TemplateVersion)
            throw new ArgumentException("The validation form version does not match its snapshot.");
        if (!string.Equals(request.ProductTypeCode, request.FormSnapshot.ProductTypeCode, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The validation product type does not match its snapshot.");
        entity.SetValidationRequest(request.TemplateVersionId, request.TemplateVersion, request.ProductTypeCode,
            JsonSerializer.Serialize(request, PayloadJsonOptions), now);
    }

    private static void ValidateValidationRequestForSubmit(VerificationApplication entity)
    {
        if (string.IsNullOrWhiteSpace(entity.ValidationFormPayloadJson)) return;
        var request = JsonSerializer.Deserialize<ValidationRequestSubmissionDto>(entity.ValidationFormPayloadJson, PayloadJsonOptions)
            ?? throw new InvalidOperationException("The validation form data is invalid.");
        var snapshot = request.FormSnapshot ?? throw new InvalidOperationException("The validation form snapshot is missing.");
        var missing = snapshot.Sections.SelectMany(x => x.Fields).SelectMany(Flatten)
            .Where(x => x.IsEnabled && IsVisible(x, request.FieldValues) && IsRequired(x, request.FieldValues))
            .Where(x => x.FieldType is not ("Attachment" or "Group" or "RepeatableGroup"))
            .Where(x => !request.FieldValues.TryGetValue(x.Code, out var values) || values.All(string.IsNullOrWhiteSpace))
            .Select(x => x.DisplayLabel).ToList();
        var missingTests = snapshot.TestCategories.SelectMany(x => x.TestItems)
            .Where(x => x.IsEnabled && x.IsMandatory && !request.SelectedTestItemCodes.Contains(x.Code))
            .Select(x => x.DisplayLabel).ToList();
        var missingParameters = snapshot.TestCategories.SelectMany(x => x.TestItems)
            .Where(x => x.IsEnabled && request.SelectedTestItemCodes.Contains(x.Code))
            .SelectMany(item => item.Parameters.SelectMany(FlattenParameters)
                .Where(x => x.IsEnabled && x.IsRequired)
                .Where(x => !request.TestParameterValues.TryGetValue(item.Code, out var values) ||
                            !values.TryGetValue(x.Code, out var answers) || answers.All(string.IsNullOrWhiteSpace))
                .Select(x => $"{item.DisplayLabel}／{x.DisplayLabel}"))
            .ToList();
        if (missing.Count > 0 || missingTests.Count > 0 || missingParameters.Count > 0)
            throw new InvalidOperationException($"新版申請單尚未完整填寫：{string.Join("、", missing.Concat(missingTests).Concat(missingParameters))}");
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

    private static void EnsureCsitStaff(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true ||
            !user.HasClaim(SystemAuthorization.AccessScopeClaim, SystemAuthorization.AccessScopes.CsitStaff))
            throw new UnauthorizedAccessException("CSIT staff access is required.");
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;
    private static string NormalizeAccount(string value) => value.Trim().ToLowerInvariant();

    private async Task<VerificationApplicationDto> MapWithDraftOptionsAsync(VerificationApplication entity, CancellationToken cancellationToken)
    {
        var dto = Map(entity);
        if (entity.Status is VerificationApplicationStatus.Draft or VerificationApplicationStatus.Returned)
        {
            var options = await _db.SystemOptions.AsNoTracking()
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
            UploadedBy = x.UploadedBy
        }).ToList()
    };
}
