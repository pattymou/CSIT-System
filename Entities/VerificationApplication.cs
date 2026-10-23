namespace SIT.DepartmentSystem.Web.Entities;

// Team/Leader comes from the system Team master; ModuleCode comes from the workflow rule.
// Both are resolved and snapshotted at Submit/Resubmit time.
public sealed record VerificationApplicationRouting(
    Guid RoutingDepartmentOptionId,
    string RoutingDepartmentCode,
    string RoutingDepartmentName,
    Guid TeamOptionId,
    string TeamCode,
    string TeamName,
    string ModuleCode,
    string AssignedLeaderAccount,
    string? AssignedLeaderDisplayName);

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
    public string? ModuleCode { get; private set; }
    public Guid? RoutingDepartmentOptionId { get; private set; }
    public string? RoutingDepartmentCode { get; private set; }
    public string? RoutingDepartmentName { get; private set; }
    public Guid? TeamOptionId { get; private set; }
    public string? TeamCode { get; private set; }
    public string? TeamName { get; private set; }
    // Deprecated VerificationCategory snapshot columns are retained for historical rows.
    public Guid? VerificationCategoryId { get; private set; }
    public string? CategoryCode { get; private set; }
    public string? CategoryName { get; private set; }
    public string? AssignedLeaderAccount { get; private set; }
    public string? AssignedLeaderDisplayName { get; private set; }

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
    public DateOnly? ExpectedEndDate { get; private set; }
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
    public Guid? ValidationTemplateVersionId { get; private set; }
    public int? ValidationTemplateVersionNumber { get; private set; }
    public string? ValidationProductTypeCode { get; private set; }
    public string? ValidationFormPayloadJson { get; private set; }

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
        Guid? routingDepartmentOptionId,
        Guid? teamOptionId,
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
            RoutingDepartmentOptionId = routingDepartmentOptionId,
            TeamOptionId = teamOptionId,
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

    public void UpdateContent(Guid? routingDepartmentOptionId, Guid? teamOptionId, VerificationApplicationContent content, DateTime now)
    {
        EnsureStatus(VerificationApplicationStatus.Draft, VerificationApplicationStatus.Returned);
        RoutingDepartmentOptionId = routingDepartmentOptionId;
        TeamOptionId = teamOptionId;
        ApplyContent(content);
        UpdatedAt = now;
    }

    public void UpdateDraftRouting(Guid routingDepartmentOptionId, Guid teamOptionId, DateTime now)
    {
        EnsureStatus(VerificationApplicationStatus.Draft, VerificationApplicationStatus.Returned);
        RoutingDepartmentOptionId = routingDepartmentOptionId;
        TeamOptionId = teamOptionId;
        UpdatedAt = now;
    }

    public void SetValidationRequest(Guid templateVersionId, int templateVersionNumber, string productTypeCode, string payloadJson, DateTime now)
    {
        EnsureStatus(VerificationApplicationStatus.Draft, VerificationApplicationStatus.Returned);
        if (templateVersionId == Guid.Empty) throw new ArgumentException("Template version is required.", nameof(templateVersionId));
        if (templateVersionNumber <= 0) throw new ArgumentOutOfRangeException(nameof(templateVersionNumber));
        if (string.IsNullOrWhiteSpace(productTypeCode)) throw new ArgumentException("Product type is required.", nameof(productTypeCode));
        if (string.IsNullOrWhiteSpace(payloadJson)) throw new ArgumentException("Validation form payload is required.", nameof(payloadJson));
        ValidationTemplateVersionId = templateVersionId;
        ValidationTemplateVersionNumber = templateVersionNumber;
        ValidationProductTypeCode = productTypeCode.Trim();
        ValidationFormPayloadJson = payloadJson;
        UpdatedAt = now;
    }

    public void UpdateSubmittedContent(
        VerificationApplicationRouting routing,
        VerificationApplicationContent content,
        Guid templateVersionId,
        int templateVersionNumber,
        string productTypeCode,
        string payloadJson,
        DateTime now)
    {
        EnsureStatus(VerificationApplicationStatus.Submitted);
        ArgumentNullException.ThrowIfNull(routing);
        ApplyContent(content);
        RoutingDepartmentOptionId = routing.RoutingDepartmentOptionId;
        TeamOptionId = routing.TeamOptionId;
        ApplyRouting(routing);
        SetValidationRequestValues(templateVersionId, templateVersionNumber, productTypeCode, payloadJson);
        EnsureSubmitRequirements();
        UpdatedAt = now;
    }

    public void Submit(VerificationApplicationRouting routing, DateTime now)
    {
        EnsureStatus(VerificationApplicationStatus.Draft, VerificationApplicationStatus.Returned);
        ArgumentNullException.ThrowIfNull(routing);
        if (TeamOptionId != routing.TeamOptionId)
            throw new InvalidOperationException("Resolved Team does not match the application.");
        if (RoutingDepartmentOptionId != routing.RoutingDepartmentOptionId)
            throw new InvalidOperationException("Resolved Department does not match the application.");
        ApplyRouting(routing);
        EnsureSubmitRequirements();
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

    public void Accept(Guid moduleRecordId, DateOnly expectedEndDate, string processedBy, DateTime now, string? processingNote = null)
    {
        EnsureStatus(VerificationApplicationStatus.Submitted);
        if (ModuleRecordId.HasValue)
        {
            throw new InvalidOperationException("The application already has a ModuleRecord.");
        }

        EnsureProcessedBy(processedBy);
        ModuleRecordId = moduleRecordId;
        ExpectedEndDate = expectedEndDate;
        Status = VerificationApplicationStatus.Accepted;
        AcceptedAt = now;
        SetProcessing(processedBy, processingNote, now);
    }

    private void ApplyRouting(VerificationApplicationRouting routing)
    {
        RoutingDepartmentCode = RequiredRoutingValue(routing.RoutingDepartmentCode, nameof(routing.RoutingDepartmentCode));
        RoutingDepartmentName = RequiredRoutingValue(routing.RoutingDepartmentName, nameof(routing.RoutingDepartmentName));
        TeamCode = RequiredRoutingValue(routing.TeamCode, nameof(routing.TeamCode));
        TeamName = RequiredRoutingValue(routing.TeamName, nameof(routing.TeamName));
        ModuleCode = RequiredRoutingValue(routing.ModuleCode, nameof(routing.ModuleCode));
        AssignedLeaderAccount = RequiredRoutingValue(routing.AssignedLeaderAccount, nameof(routing.AssignedLeaderAccount));
        AssignedLeaderDisplayName = Clean(routing.AssignedLeaderDisplayName);
    }

    private void SetValidationRequestValues(Guid templateVersionId, int templateVersionNumber, string productTypeCode, string payloadJson)
    {
        if (templateVersionId == Guid.Empty) throw new ArgumentException("Template version is required.", nameof(templateVersionId));
        if (templateVersionNumber <= 0) throw new ArgumentOutOfRangeException(nameof(templateVersionNumber));
        if (string.IsNullOrWhiteSpace(productTypeCode)) throw new ArgumentException("Product type is required.", nameof(productTypeCode));
        if (string.IsNullOrWhiteSpace(payloadJson)) throw new ArgumentException("Validation form payload is required.", nameof(payloadJson));
        ValidationTemplateVersionId = templateVersionId;
        ValidationTemplateVersionNumber = templateVersionNumber;
        ValidationProductTypeCode = productTypeCode.Trim();
        ValidationFormPayloadJson = payloadJson;
    }

    private void EnsureSubmitRequirements()
    {
        var missing = new List<string>();
        AddIfMissing(missing, ApplicantAccount, nameof(ApplicantAccount));
        AddIfMissing(missing, ApplicantName, nameof(ApplicantName));
        AddIfMissing(missing, ApplicantEmail, nameof(ApplicantEmail));
        AddIfMissing(missing, Department, nameof(Department));
        AddIfMissing(missing, ProjectName, nameof(ProjectName));
        AddIfMissing(missing, Customer, nameof(Customer));
        AddIfMissing(missing, ProductModel, nameof(ProductModel));
        if (string.IsNullOrWhiteSpace(ValidationFormPayloadJson))
        {
            AddIfMissing(missing, ValidationRequirement, nameof(ValidationRequirement));
            if (!RequestedFinishDate.HasValue) missing.Add(nameof(RequestedFinishDate));
        }
        if (!RoutingDepartmentOptionId.HasValue) missing.Add(nameof(RoutingDepartmentOptionId));
        AddIfMissing(missing, RoutingDepartmentCode, nameof(RoutingDepartmentCode));
        AddIfMissing(missing, RoutingDepartmentName, nameof(RoutingDepartmentName));
        if (!TeamOptionId.HasValue) missing.Add(nameof(TeamOptionId));
        AddIfMissing(missing, TeamCode, nameof(TeamCode));
        AddIfMissing(missing, TeamName, nameof(TeamName));
        AddIfMissing(missing, ModuleCode, nameof(ModuleCode));
        AddIfMissing(missing, AssignedLeaderAccount, nameof(AssignedLeaderAccount));

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
    private static string RequiredRoutingValue(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"Routing configuration is missing {name}.") : value.Trim();

    private void EnsureStatus(params VerificationApplicationStatus[] allowed)
    {
        if (!allowed.Contains(Status))
        {
            throw new InvalidOperationException($"Cannot transition application from {Status}.");
        }
    }
}
