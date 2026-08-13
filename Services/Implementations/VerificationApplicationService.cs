using System.Data;
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

    public VerificationApplicationService(
        AppDbContext db,
        IModuleRecordCreationService moduleRecordCreation)
    {
        _db = db;
        _moduleRecordCreation = moduleRecordCreation;
    }

    public async Task<VerificationApplicationDto> CreateDraftAsync(
        ApplicantSnapshot applicant,
        VerificationApplicationTarget target,
        CreateVerificationApplicationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(applicant);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(request);
        Require(target.ModuleCode, nameof(target.ModuleCode));

        var moduleCode = target.ModuleCode.Trim();
        if (!await _db.Modules.AnyAsync(x => x.Code == moduleCode, cancellationToken))
        {
            throw new ArgumentException("Unknown target module code.", nameof(target));
        }

        var now = DateTime.UtcNow;
        var sequence = await _db.Database
            .SqlQueryRaw<long>("SELECT nextval('verification_application_no_seq') AS \"Value\"")
            .SingleAsync(cancellationToken);

        var entity = VerificationApplication.CreateDraft(
            Guid.NewGuid(),
            $"VA-{now:yyyyMMdd}-{sequence:D6}",
            moduleCode,
            Normalize(applicant.ApplicantAccount),
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
        UpdateVerificationApplicationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var entity = await FindRequiredAsync(id, cancellationToken);
        entity.UpdateContent(MapContent(request), DateTime.UtcNow);
        await _db.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<VerificationApplicationDto> SubmitAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await FindRequiredAsync(id, cancellationToken);
        var targetModuleExists = !string.IsNullOrWhiteSpace(entity.ModuleCode)
            && await _db.Modules.AnyAsync(x => x.Code == entity.ModuleCode, cancellationToken);
        entity.Submit(targetModuleExists, DateTime.UtcNow);
        await _db.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<VerificationApplicationDto> ReturnAsync(
        Guid id,
        string processedBy,
        string? note,
        CancellationToken cancellationToken = default)
    {
        var entity = await FindRequiredAsync(id, cancellationToken);
        entity.Return(processedBy, note, DateTime.UtcNow);
        await _db.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<VerificationApplicationDto> RejectAsync(
        Guid id,
        string processedBy,
        string? note,
        CancellationToken cancellationToken = default)
    {
        var entity = await FindRequiredAsync(id, cancellationToken);
        entity.Reject(processedBy, note, DateTime.UtcNow);
        await _db.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<VerificationApplicationDto> AcceptAsync(
        Guid id,
        string processedBy,
        CancellationToken cancellationToken = default)
    {
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

            if (entity.ModuleRecordId.HasValue)
            {
                throw new InvalidOperationException("The application already has a ModuleRecord.");
            }

            var moduleRecord = await _moduleRecordCreation.CreateAsync(new ModuleRecordCreationRequest
            {
                ModuleCode = entity.ModuleCode,
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

            entity.Accept(moduleRecord.Id, processedBy, DateTime.UtcNow);
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

    public async Task<VerificationApplicationDto?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _db.VerificationApplications
            .AsNoTracking()
            .Include(x => x.Files)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async Task<ListResponseDto<VerificationApplicationDto>> ListAsync(
        VerificationApplicationStatus? status,
        string? applicantAccount,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var query = _db.VerificationApplications.AsNoTracking();

        if (status.HasValue)
        {
            query = query.Where(x => x.Status == status.Value);
        }

        if (!string.IsNullOrWhiteSpace(applicantAccount))
        {
            var account = applicantAccount.Trim();
            query = query.Where(x => x.ApplicantAccount == account);
        }

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

    private async Task<VerificationApplication> FindRequiredAsync(Guid id, CancellationToken cancellationToken) =>
        await _db.VerificationApplications.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
        ?? throw new KeyNotFoundException($"Verification application {id} was not found.");

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{name} is required.", name);
        }
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

    private static VerificationApplicationDto Map(VerificationApplication entity) => new()
    {
        Id = entity.Id,
        ApplicationNo = entity.ApplicationNo,
        ModuleCode = entity.ModuleCode,
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
