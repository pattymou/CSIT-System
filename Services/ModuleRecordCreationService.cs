using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Services;

public sealed class ModuleRecordCreationRequest
{
    public string ModuleCode { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Customer { get; init; }
    public string? Owner { get; init; }
    public string? PmSales { get; init; }
    public string Status { get; init; } = "Open";
    public string? Result { get; init; }
    public int Progress { get; init; }
    public DateOnly? StartDate { get; init; }
    public DateOnly? ExpectedEndDate { get; init; }
    public DateOnly? SampleReadyDate { get; init; }
    public string? Note { get; init; }
    public string? ApplicantNote { get; init; }
    public string? Team { get; init; }
    public string? Npi { get; init; }
    public string? HardwareVersion { get; init; }
    public string? SoftwareVersion { get; init; }
    public string? HardwareEngineer { get; init; }
    public string? SoftwareEngineer { get; init; }
    public string? Pjm { get; init; }
    public string? Location { get; init; }
    public string? RequestDepartment { get; init; }
    public string? RequestApplicant { get; init; }
    public string? SubPu { get; init; }
    public string? AssignOwner { get; init; }
    public string? MechanicalEngineer { get; init; }
    public string? Department { get; init; }
    public string? FirmwareVersion { get; init; }
    public string? WirelessDrive { get; init; }
    public string? CustomerProductName { get; init; }
    public string? Chipset { get; init; }
    public string? SampleMacAddress { get; init; }
    public string? UtilityVersion { get; init; }
    public string? DspModel { get; init; }
    public string? DqaOwner { get; init; }
    public string? JiraLink { get; init; }
    public string? NotifyUsers { get; init; }
}

public interface IModuleRecordCreationService
{
    Task<ModuleRecord> CreateAsync(ModuleRecordCreationRequest request, CancellationToken cancellationToken = default);
}

public sealed class ModuleRecordCreationService : IModuleRecordCreationService
{
    private readonly AppDbContext _db;

    public ModuleRecordCreationService(AppDbContext db) => _db = db;

    public async Task<ModuleRecord> CreateAsync(ModuleRecordCreationRequest request, CancellationToken cancellationToken = default)
    {
        var module = await _db.Modules.FirstAsync(x => x.Code == request.ModuleCode, cancellationToken);
        var now = DateTime.UtcNow;
        var entity = new ModuleRecord
        {
            Id = Guid.NewGuid(), ModuleId = module.Id, RecordNo = $"REC-{DateTime.Now:yyyyMMddHHmmss}",
            Name = request.Name.Trim(), Customer = request.Customer, Owner = request.Owner, PmSales = request.PmSales,
            Status = request.Status, Result = request.Result, Progress = request.Progress,
            StartDate = request.StartDate, ExpectedEndDate = request.ExpectedEndDate, SampleReadyDate = request.SampleReadyDate,
            Note = request.Note, ApplicantNote = request.ApplicantNote, Team = request.Team, Npi = request.Npi,
            HardwareVersion = request.HardwareVersion, SoftwareVersion = request.SoftwareVersion,
            HardwareEngineer = request.HardwareEngineer, SoftwareEngineer = request.SoftwareEngineer, Pjm = request.Pjm,
            Location = string.IsNullOrWhiteSpace(request.Location) ? "台北" : request.Location,
            RequestDepartment = request.RequestDepartment, RequestApplicant = request.RequestApplicant, SubPu = request.SubPu,
            AssignOwner = request.AssignOwner, MechanicalEngineer = request.MechanicalEngineer, Department = request.Department,
            FirmwareVersion = request.FirmwareVersion, WirelessDrive = request.WirelessDrive,
            CustomerProductName = request.CustomerProductName, Chipset = request.Chipset,
            SampleMacAddress = request.SampleMacAddress, UtilityVersion = request.UtilityVersion, DspModel = request.DspModel,
            DqaOwner = request.DqaOwner, JiraLink = request.JiraLink, NotifyUsers = request.NotifyUsers,
            CreatedAt = now, UpdatedAt = now
        };

        _db.ModuleRecords.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return entity;
    }
}
