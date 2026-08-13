using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Services.Implementations;

public class ModuleRecordService : IModuleRecordService
{
    private readonly AppDbContext _db;
    private readonly ICaseFileService _caseFileService;
    private readonly IModuleRecordCreationService _creationService;

    public ModuleRecordService(
        AppDbContext db,
        ICaseFileService caseFileService,
        IModuleRecordCreationService creationService)
    {
        _db = db;
        _caseFileService = caseFileService;
        _creationService = creationService;
    }

    public async Task<ListResponseDto<ModuleRecordListItemDto>> GetListAsync(
        string moduleCode,
        int page,
        int pageSize,
        string? status)
    {
        var module = await _db.Modules.FirstOrDefaultAsync(x => x.Code == moduleCode);

        if (module == null)
        {
            return EmptyList(page, pageSize);
        }

        var query = _db.ModuleRecords.Where(x => x.ModuleId == module.Id);

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(x => x.Status == status);
        }

        var totalCount = await query.CountAsync();

        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new ModuleRecordListItemDto
            {
                Id = x.Id,
                RecordNo = x.RecordNo,
                Name = x.Name,
                Customer = x.Customer,
                Status = x.Status,
                Progress = x.Progress,

                Team = x.Team,
                Npi = x.Npi,
                HardwareVersion = x.HardwareVersion,
                SoftwareVersion = x.SoftwareVersion,
                HardwareEngineer = x.HardwareEngineer,
                SoftwareEngineer = x.SoftwareEngineer,
                Pjm = x.Pjm,
                Location = x.Location,
                RequestDepartment = x.RequestDepartment,
                RequestApplicant = x.RequestApplicant,

                SubPu = x.SubPu,
                AssignOwner = x.AssignOwner,
                MechanicalEngineer = x.MechanicalEngineer,
                Department = x.Department,
                FirmwareVersion = x.FirmwareVersion,
                WirelessDrive = x.WirelessDrive,
                CustomerProductName = x.CustomerProductName,
                Chipset = x.Chipset,
                SampleMacAddress = x.SampleMacAddress,
                UtilityVersion = x.UtilityVersion,
                DspModel = x.DspModel,
                DqaOwner = x.DqaOwner,
                JiraLink = x.JiraLink
            })
            .ToListAsync();

        return new ListResponseDto<ModuleRecordListItemDto>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        };
    }

    public async Task<ListResponseDto<ModuleRecordListItemDto>> GetProjectViewListAsync(
        string moduleCode,
        string status,
        string site,
        string? search,
        int page,
        int pageSize)
    {
        var module = await _db.Modules.FirstOrDefaultAsync(x => x.Code == moduleCode);

        if (module == null)
        {
            return EmptyList(page, pageSize);
        }

        var siteText = site == "WJ" ? "吳江" : "台北";

        var query = _db.ModuleRecords
            .Where(x => x.ModuleId == module.Id)
            .Where(x => x.Status == status)
            .Where(x => (x.Location ?? "") == siteText);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var keyword = search.Trim();

            query = query.Where(x =>
                (x.RecordNo ?? "").Contains(keyword) ||
                (x.Name ?? "").Contains(keyword) ||
                (x.Customer ?? "").Contains(keyword) ||
                (x.RequestDepartment ?? "").Contains(keyword) ||
                (x.RequestApplicant ?? "").Contains(keyword) ||
                (x.Team ?? "").Contains(keyword) ||
                (x.Npi ?? "").Contains(keyword) ||
                (x.HardwareEngineer ?? "").Contains(keyword) ||
                (x.SoftwareEngineer ?? "").Contains(keyword) ||
                (x.Pjm ?? "").Contains(keyword) ||
                (x.AssignOwner ?? "").Contains(keyword) ||
                (x.MechanicalEngineer ?? "").Contains(keyword) ||
                (x.Department ?? "").Contains(keyword) ||
                (x.CustomerProductName ?? "").Contains(keyword) ||
                (x.Chipset ?? "").Contains(keyword) ||
                (x.JiraLink ?? "").Contains(keyword));
        }

        var totalCount = await query.CountAsync();

        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new ModuleRecordListItemDto
            {
                Id = x.Id,
                RecordNo = x.RecordNo,
                Name = x.Name,
                Customer = x.Customer,
                Status = x.Status,
                Progress = x.Progress,

                Team = x.Team,
                Npi = x.Npi,
                HardwareVersion = x.HardwareVersion,
                SoftwareVersion = x.SoftwareVersion,
                HardwareEngineer = x.HardwareEngineer,
                SoftwareEngineer = x.SoftwareEngineer,
                Pjm = x.Pjm,
                Location = x.Location,
                RequestDepartment = x.RequestDepartment,
                RequestApplicant = x.RequestApplicant,

                SubPu = x.SubPu,
                AssignOwner = x.AssignOwner,
                MechanicalEngineer = x.MechanicalEngineer,
                Department = x.Department,
                FirmwareVersion = x.FirmwareVersion,
                WirelessDrive = x.WirelessDrive,
                CustomerProductName = x.CustomerProductName,
                Chipset = x.Chipset,
                SampleMacAddress = x.SampleMacAddress,
                UtilityVersion = x.UtilityVersion,
                DspModel = x.DspModel,
                DqaOwner = x.DqaOwner,
                JiraLink = x.JiraLink
            })
            .ToListAsync();

        return new ListResponseDto<ModuleRecordListItemDto>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        };
    }

    public async Task<ModuleRecordDetailDto?> GetByIdAsync(Guid id)
    {
        var entity = await _db.ModuleRecords.FirstOrDefaultAsync(x => x.Id == id);
        if (entity == null)
        {
            return null;
        }

        return new ModuleRecordDetailDto
        {
            Id = entity.Id,
            ModuleId = entity.ModuleId,
            RecordNo = entity.RecordNo,
            Name = entity.Name,
            Customer = entity.Customer,
            Owner = entity.Owner,
            PmSales = entity.PmSales,
            Status = entity.Status,
            Result = entity.Result,
            Progress = entity.Progress,
            StartDate = entity.StartDate,
            ExpectedEndDate = entity.ExpectedEndDate,
            SampleReadyDate = entity.SampleReadyDate,
            Note = entity.Note,
            ApplicantNote = entity.ApplicantNote,

            Team = entity.Team,
            Npi = entity.Npi,
            HardwareVersion = entity.HardwareVersion,
            SoftwareVersion = entity.SoftwareVersion,
            HardwareEngineer = entity.HardwareEngineer,
            SoftwareEngineer = entity.SoftwareEngineer,
            Pjm = entity.Pjm,
            Location = entity.Location,
            RequestDepartment = entity.RequestDepartment,
            RequestApplicant = entity.RequestApplicant,

            SubPu = entity.SubPu,
            AssignOwner = entity.AssignOwner,
            MechanicalEngineer = entity.MechanicalEngineer,
            Department = entity.Department,
            FirmwareVersion = entity.FirmwareVersion,
            WirelessDrive = entity.WirelessDrive,
            CustomerProductName = entity.CustomerProductName,
            Chipset = entity.Chipset,
            SampleMacAddress = entity.SampleMacAddress,
            UtilityVersion = entity.UtilityVersion,
            DspModel = entity.DspModel,
            DqaOwner = entity.DqaOwner,
            JiraLink = entity.JiraLink,
            NotifyUsers = SplitNotifyUsers(entity.NotifyUsers)
        };
    }

    public async Task<Guid> CreateAsync(string moduleCode, ModuleRecordUpsertRequest request)
    {
        var entity = await _creationService.CreateAsync(new ModuleRecordCreationRequest
        {
            ModuleCode = moduleCode,
            Name = request.Name.Trim(),
            Customer = request.Customer,
            Owner = request.Owner,
            PmSales = request.PmSales,
            Status = string.IsNullOrWhiteSpace(request.Status) ? "Open" : request.Status,
            Result = request.Result,
            Progress = request.Progress,
            StartDate = request.StartDate,
            ExpectedEndDate = request.ExpectedEndDate,
            SampleReadyDate = request.SampleReadyDate,
            Note = request.Note,
            ApplicantNote = request.ApplicantNote,

            Team = request.Team,
            Npi = request.Npi,
            HardwareVersion = request.HardwareVersion,
            SoftwareVersion = request.SoftwareVersion,
            HardwareEngineer = request.HardwareEngineer,
            SoftwareEngineer = request.SoftwareEngineer,
            Pjm = request.Pjm,
            Location = string.IsNullOrWhiteSpace(request.Location) ? "台北" : request.Location,
            RequestDepartment = request.RequestDepartment,
            RequestApplicant = request.RequestApplicant,

            SubPu = request.SubPu,
            AssignOwner = request.AssignOwner,
            MechanicalEngineer = request.MechanicalEngineer,
            Department = request.Department,
            FirmwareVersion = request.FirmwareVersion,
            WirelessDrive = request.WirelessDrive,
            CustomerProductName = request.CustomerProductName,
            Chipset = request.Chipset,
            SampleMacAddress = request.SampleMacAddress,
            UtilityVersion = request.UtilityVersion,
            DspModel = request.DspModel,
            DqaOwner = request.DqaOwner,
            JiraLink = request.JiraLink,
            NotifyUsers = JoinNotifyUsers(request.NotifyUsers)
        });

        return entity.Id;
    }

    public async Task<bool> UpdateAsync(Guid id, ModuleRecordUpsertRequest request)
    {
        var entity = await _db.ModuleRecords.FirstOrDefaultAsync(x => x.Id == id);
        if (entity == null)
        {
            return false;
        }

        entity.Name = request.Name.Trim();
        entity.Customer = request.Customer;
        entity.Owner = request.Owner;
        entity.PmSales = request.PmSales;
        entity.Status = string.IsNullOrWhiteSpace(request.Status) ? "Open" : request.Status;
        entity.Result = request.Result;
        entity.Progress = request.Progress;
        entity.StartDate = request.StartDate;
        entity.ExpectedEndDate = request.ExpectedEndDate;
        entity.SampleReadyDate = request.SampleReadyDate;
        entity.Note = request.Note;
        entity.ApplicantNote = request.ApplicantNote;

        entity.Team = request.Team;
        entity.Npi = request.Npi;
        entity.HardwareVersion = request.HardwareVersion;
        entity.SoftwareVersion = request.SoftwareVersion;
        entity.HardwareEngineer = request.HardwareEngineer;
        entity.SoftwareEngineer = request.SoftwareEngineer;
        entity.Pjm = request.Pjm;
        entity.Location = string.IsNullOrWhiteSpace(request.Location) ? "台北" : request.Location;
        entity.RequestDepartment = request.RequestDepartment;
        entity.RequestApplicant = request.RequestApplicant;

        entity.SubPu = request.SubPu;
        entity.AssignOwner = request.AssignOwner;
        entity.MechanicalEngineer = request.MechanicalEngineer;
        entity.Department = request.Department;
        entity.FirmwareVersion = request.FirmwareVersion;
        entity.WirelessDrive = request.WirelessDrive;
        entity.CustomerProductName = request.CustomerProductName;
        entity.Chipset = request.Chipset;
        entity.SampleMacAddress = request.SampleMacAddress;
        entity.UtilityVersion = request.UtilityVersion;
        entity.DspModel = request.DspModel;
        entity.DqaOwner = request.DqaOwner;
        entity.JiraLink = request.JiraLink;
        entity.NotifyUsers = JoinNotifyUsers(request.NotifyUsers);

        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        await _caseFileService.RebuildProjectRawDataAsync(entity.Id);

        return true;
    }

    public async Task<bool> DeleteAsync(Guid recordId)
    {
        Console.WriteLine($"[ModuleRecordService] Delete record start. recordId={recordId}");

        var record = await _db.ModuleRecords
            .FirstOrDefaultAsync(x => x.Id == recordId);

        if (record == null)
        {
            Console.WriteLine($"[ModuleRecordService] Record not found. recordId={recordId}");
            return false;
        }

        var cases = await _db.ModuleRecordCases
            .Where(x => x.RecordId == recordId)
            .ToListAsync();

        var caseIds = cases.Select(x => x.Id).ToList();

        var tasks = await _db.ModuleRecordTasks
            .Where(x => caseIds.Contains(x.CaseId))
            .ToListAsync();

        await _caseFileService.DeleteFilesByRecordIdAsync(recordId);

        _db.ModuleRecordTasks.RemoveRange(tasks);
        _db.ModuleRecordCases.RemoveRange(cases);
        _db.ModuleRecords.Remove(record);

        await _db.SaveChangesAsync();

        Console.WriteLine($"[ModuleRecordService] Delete record done. recordId={recordId}, cases={cases.Count}, tasks={tasks.Count}");

        return true;
    }

    private static ListResponseDto<ModuleRecordListItemDto> EmptyList(int page, int pageSize)
    {
        return new ListResponseDto<ModuleRecordListItemDto>
        {
            Items = new(),
            Page = page,
            PageSize = pageSize,
            TotalCount = 0
        };
    }

    private static string? JoinNotifyUsers(List<string>? users)
    {
        if (users == null || users.Count == 0)
        {
            return null;
        }

        var cleaned = users
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return cleaned.Count == 0 ? null : string.Join(",", cleaned);
    }

    private static List<string> SplitNotifyUsers(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new List<string>();
        }

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
