using System.Data;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Services.Implementations;

public sealed class VerificationApplicationService : IVerificationApplicationService
{
    private readonly AppDbContext _db;
    private readonly IModuleRecordCreationService _moduleRecordCreation;
    private readonly ISystemOptionService _systemOptions;

    public VerificationApplicationService(
        AppDbContext db,
        IModuleRecordCreationService moduleRecordCreation,
        ISystemOptionService systemOptions)
    {
        _db = db;
        _moduleRecordCreation = moduleRecordCreation;
        _systemOptions = systemOptions;
    }

    public async Task<VerificationApplicationDto> CreateDraftAsync(
        ClaimsPrincipal user,
        CreateVerificationApplicationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TeamOptionId == Guid.Empty)
            throw new ArgumentException("TeamOptionId is required.", nameof(request));
        var applicant = await ResolveApplicantAsync(user, cancellationToken);
        await EnsureTeamExistsAsync(request.TeamOptionId, cancellationToken);

        var now = DateTime.UtcNow;
        var sequence = await _db.Database
            .SqlQueryRaw<long>("SELECT nextval('verification_application_no_seq') AS \"Value\"")
            .SingleAsync(cancellationToken);

        var entity = VerificationApplication.CreateDraft(
            Guid.NewGuid(),
            $"VA-{now:yyyyMMdd}-{sequence:D6}",
            request.TeamOptionId,
            NormalizeAccount(applicant.ApplicantAccount),
            Normalize(applicant.ApplicantName),
            Normalize(applicant.ApplicantEmail),
            Normalize(applicant.Department),
            Clean(applicant.ApplicantExtension),
            MapContent(request),
            now);

        _db.VerificationApplications.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<VerificationApplicationDto> UpdateDraftAsync(
        Guid id,
        ClaimsPrincipal user,
        UpdateVerificationApplicationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TeamOptionId == Guid.Empty)
            throw new ArgumentException("TeamOptionId is required.", nameof(request));
        var account = VerificationApplicationSecurity.GetAccount(user);
        var entity = await FindApplicantApplicationRequiredAsync(id, account, cancellationToken);
        await EnsureTeamExistsAsync(request.TeamOptionId, cancellationToken);
        entity.UpdateContent(request.TeamOptionId, MapContent(request), DateTime.UtcNow);
        await _db.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<VerificationApplicationDto> SubmitAsync(Guid id, ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        var account = VerificationApplicationSecurity.GetAccount(user);
        var entity = await FindApplicantApplicationRequiredAsync(id, account, cancellationToken);
        var routing = await ResolveRoutingAsync(entity.TeamOptionId, cancellationToken);
        entity.Submit(routing, DateTime.UtcNow);
        await _db.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<VerificationApplicationDto> ReturnAsync(
        Guid id,
        ClaimsPrincipal user,
        string? note,
        CancellationToken cancellationToken = default)
    {
        var account = VerificationApplicationSecurity.GetAccount(user);
        var entity = await FindLeaderApplicationRequiredAsync(id, account, cancellationToken);
        entity.Return(account, note, DateTime.UtcNow);
        await _db.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<VerificationApplicationDto> RejectAsync(
        Guid id,
        ClaimsPrincipal user,
        string? note,
        CancellationToken cancellationToken = default)
    {
        var account = VerificationApplicationSecurity.GetAccount(user);
        var entity = await FindLeaderApplicationRequiredAsync(id, account, cancellationToken);
        entity.Reject(account, note, DateTime.UtcNow);
        await _db.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<VerificationApplicationDto> AcceptAsync(
        Guid id,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
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
                ExpectedEndDate = entity.RequestedFinishDate,
                SampleReadyDate = entity.SampleReadyDate,
                ApplicantNote = entity.ValidationRequirement,
                Npi = entity.Npi,
                HardwareVersion = entity.HardwareVersion,
                SoftwareVersion = entity.SoftwareVersion,
                Location = entity.Location,
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

            entity.Accept(moduleRecord.Id, account, DateTime.UtcNow);
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Map(entity);
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
        return entity is null ? null : Map(entity);
    }

    public async Task<VerificationApplicationDto?> GetForLeaderReviewAsync(Guid id, ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        var account = VerificationApplicationSecurity.GetAccount(user);
        var entity = await _db.VerificationApplications.AsNoTracking().Include(x => x.Files)
            .SingleOrDefaultAsync(x => x.Id == id && x.Status == VerificationApplicationStatus.Submitted && x.AssignedLeaderAccount == account, cancellationToken);
        return entity is null ? null : Map(entity);
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

    private async Task EnsureTeamExistsAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await _systemOptions.TeamExistsAsync(id, cancellationToken))
            throw new InvalidOperationException("Team does not exist.");
    }

    private async Task<VerificationApplicationRouting> ResolveRoutingAsync(Guid? teamOptionId, CancellationToken cancellationToken)
    {
        if (!teamOptionId.HasValue) throw new InvalidOperationException("Team is required before submit.");
        var teamLeader = await _systemOptions.GetActiveTeamLeaderAsync(teamOptionId.Value, cancellationToken);
        var moduleCode = await _db.Modules.AsNoTracking()
            .Where(x => x.Code == VerificationApplicationWorkflow.ModuleCode && x.IsEnabled)
            .Select(x => x.Code)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("The Verification Application module is missing or disabled.");

        return new VerificationApplicationRouting(
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

    private static VerificationApplicationContent MapContent(VerificationApplicationContentRequest request) => new()
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

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;
    private static string NormalizeAccount(string value) => value.Trim().ToLowerInvariant();

    private static VerificationApplicationDto Map(VerificationApplication entity) => new()
    {
        Id = entity.Id,
        ApplicationNo = entity.ApplicationNo,
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
