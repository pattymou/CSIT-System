namespace SIT.DepartmentSystem.Web.Entities;

public sealed class VerificationApplicationContent
{
    public string ProjectName { get; init; } = string.Empty;
    public string? SubPu { get; init; }
    public string? Customer { get; init; }
    public string? ProductModel { get; init; }
    public DateOnly? RequestedFinishDate { get; init; }
    public string? ValidationRequirement { get; init; }
    public string? HardwareVersion { get; init; }
    public string? FirmwareVersion { get; init; }
    public string? SoftwareVersion { get; init; }
    public DateOnly? SampleReadyDate { get; init; }
    public string? JiraLink { get; init; }
    public string? Location { get; init; }
    public string? Npi { get; init; }
    public string? WirelessDrive { get; init; }
    public string? Chipset { get; init; }
    public string? SampleMacAddress { get; init; }
    public string? UtilityVersion { get; init; }
    public string? DspModel { get; init; }
}

public class VerificationApplication
{
    private VerificationApplication() { }

    public Guid Id { get; private set; }
    public string ApplicationNo { get; private set; } = string.Empty;
    public string ModuleCode { get; private set; } = string.Empty;

    public string ApplicantAccount { get; private set; } = string.Empty;
    public string ApplicantName { get; private set; } = string.Empty;
    public string ApplicantEmail { get; private set; } = string.Empty;
    public string Department { get; private set; } = string.Empty;
    public string? ApplicantExtension { get; private set; }

    public string ProjectName { get; private set; } = string.Empty;
    public string? SubPu { get; private set; }
    public string? Customer { get; private set; }
    public string? ProductModel { get; private set; }
    public DateOnly? RequestedFinishDate { get; private set; }
    public string? ValidationRequirement { get; private set; }
    public string? HardwareVersion { get; private set; }
    public string? FirmwareVersion { get; private set; }
    public string? SoftwareVersion { get; private set; }
    public DateOnly? SampleReadyDate { get; private set; }
    public string? JiraLink { get; private set; }
    public string? Location { get; private set; }
    public string? Npi { get; private set; }
    public string? WirelessDrive { get; private set; }
    public string? Chipset { get; private set; }
    public string? SampleMacAddress { get; private set; }
    public string? UtilityVersion { get; private set; }
    public string? DspModel { get; private set; }

    public VerificationApplicationStatus Status { get; private set; } = VerificationApplicationStatus.Draft;
    public Guid? ModuleRecordId { get; private set; }
    public DateTime? SubmittedAt { get; private set; }
    public DateTime? ReturnedAt { get; private set; }
    public DateTime? RejectedAt { get; private set; }
    public DateTime? AcceptedAt { get; private set; }
    public DateTime? ProcessedAt { get; private set; }
    public string? ProcessedBy { get; private set; }
    public string? ProcessingNote { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    public ModuleRecord? ModuleRecord { get; private set; }
    public ICollection<VerificationApplicationFile> Files { get; private set; } = new List<VerificationApplicationFile>();

    public static VerificationApplication CreateDraft(
        Guid id,
        string applicationNo,
        string moduleCode,
        string applicantAccount,
        string applicantName,
        string applicantEmail,
        string department,
        string? applicantExtension,
        VerificationApplicationContent content,
        DateTime now)
    {
        var entity = new VerificationApplication
        {
            Id = id,
            ApplicationNo = applicationNo,
            ModuleCode = moduleCode,
            ApplicantAccount = applicantAccount,
            ApplicantName = applicantName,
            ApplicantEmail = applicantEmail,
            Department = department,
            ApplicantExtension = applicantExtension,
            CreatedAt = now,
            UpdatedAt = now
        };
        entity.ApplyContent(content);
        return entity;
    }

    public void UpdateContent(VerificationApplicationContent content, DateTime now)
    {
        EnsureStatus(VerificationApplicationStatus.Draft, VerificationApplicationStatus.Returned);
        ApplyContent(content);
        UpdatedAt = now;
    }

    public void Submit(bool targetModuleExists, DateTime now)
    {
        EnsureStatus(VerificationApplicationStatus.Draft, VerificationApplicationStatus.Returned);
        EnsureSubmitRequirements(targetModuleExists);
        Status = VerificationApplicationStatus.Submitted;
        SubmittedAt = now;
        ProcessedAt = null;
        ProcessedBy = null;
        ProcessingNote = null;
        UpdatedAt = now;
    }

    public void Return(string processedBy, string? note, DateTime now)
    {
        EnsureStatus(VerificationApplicationStatus.Submitted);
        EnsureProcessedBy(processedBy);
        EnsureProcessingNote(note);
        Status = VerificationApplicationStatus.Returned;
        ReturnedAt = now;
        SetProcessing(processedBy, note, now);
    }

    public void Reject(string processedBy, string? note, DateTime now)
    {
        EnsureStatus(VerificationApplicationStatus.Submitted);
        EnsureProcessedBy(processedBy);
        EnsureProcessingNote(note);
        Status = VerificationApplicationStatus.Rejected;
        RejectedAt = now;
        SetProcessing(processedBy, note, now);
    }

    public void Accept(Guid moduleRecordId, string processedBy, DateTime now)
    {
        EnsureStatus(VerificationApplicationStatus.Submitted);
        if (ModuleRecordId.HasValue)
        {
            throw new InvalidOperationException("The application already has a ModuleRecord.");
        }

        EnsureProcessedBy(processedBy);
        ModuleRecordId = moduleRecordId;
        Status = VerificationApplicationStatus.Accepted;
        AcceptedAt = now;
        SetProcessing(processedBy, null, now);
    }

    private void EnsureSubmitRequirements(bool targetModuleExists)
    {
        var missing = new List<string>();
        AddIfMissing(missing, ApplicantAccount, nameof(ApplicantAccount));
        AddIfMissing(missing, ApplicantName, nameof(ApplicantName));
        AddIfMissing(missing, ApplicantEmail, nameof(ApplicantEmail));
        AddIfMissing(missing, Department, nameof(Department));
        AddIfMissing(missing, ProjectName, nameof(ProjectName));
        AddIfMissing(missing, Customer, nameof(Customer));
        AddIfMissing(missing, ProductModel, nameof(ProductModel));
        AddIfMissing(missing, ValidationRequirement, nameof(ValidationRequirement));
        if (!RequestedFinishDate.HasValue) missing.Add(nameof(RequestedFinishDate));
        if (string.IsNullOrWhiteSpace(ModuleCode) || !targetModuleExists) missing.Add(nameof(ModuleCode));

        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"Application cannot be submitted. Missing or invalid: {string.Join(", ", missing)}.");
        }
    }

    private void ApplyContent(VerificationApplicationContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        ProjectName = content.ProjectName?.Trim() ?? string.Empty;
        SubPu = Clean(content.SubPu);
        Customer = Clean(content.Customer);
        ProductModel = Clean(content.ProductModel);
        RequestedFinishDate = content.RequestedFinishDate;
        ValidationRequirement = Clean(content.ValidationRequirement);
        HardwareVersion = Clean(content.HardwareVersion);
        FirmwareVersion = Clean(content.FirmwareVersion);
        SoftwareVersion = Clean(content.SoftwareVersion);
        SampleReadyDate = content.SampleReadyDate;
        JiraLink = Clean(content.JiraLink);
        Location = Clean(content.Location);
        Npi = Clean(content.Npi);
        WirelessDrive = Clean(content.WirelessDrive);
        Chipset = Clean(content.Chipset);
        SampleMacAddress = Clean(content.SampleMacAddress);
        UtilityVersion = Clean(content.UtilityVersion);
        DspModel = Clean(content.DspModel);
    }

    private void SetProcessing(string processedBy, string? note, DateTime now)
    {
        ProcessedBy = processedBy.Trim();
        ProcessingNote = Clean(note);
        ProcessedAt = now;
        UpdatedAt = now;
    }

    private static void EnsureProcessedBy(string processedBy)
    {
        if (string.IsNullOrWhiteSpace(processedBy))
        {
            throw new ArgumentException("ProcessedBy is required.", nameof(processedBy));
        }
    }

    private static void EnsureProcessingNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            throw new ArgumentException("ProcessingNote is required.", nameof(note));
        }
    }

    private static void AddIfMissing(ICollection<string> missing, string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) missing.Add(name);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void EnsureStatus(params VerificationApplicationStatus[] allowed)
    {
        if (!allowed.Contains(Status))
        {
            throw new InvalidOperationException($"Cannot transition application from {Status}.");
        }
    }
}
