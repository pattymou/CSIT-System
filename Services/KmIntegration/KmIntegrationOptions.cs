namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public sealed class KmIntegrationOptions
{
    public const string SectionName = "KmIntegration";
    public bool Enabled { get; set; }
    public bool VendorEnabled { get; set; }
    public string? BaseUrl { get; set; }
    public string? ServiceBaseUrl { get; set; }
    public int TimeoutSeconds { get; set; } = 15;
    public int MaxRetryCount { get; set; } = 8;
    public int[] RetryScheduleSeconds { get; set; } = [60, 300, 900, 3600];
    public int PollSeconds { get; set; } = 30;
    public int LeaseSeconds { get; set; } = 600;
    public int ArchivePollSeconds { get; set; } = 30;
    public int ArchiveLeaseSeconds { get; set; } = 600;
    public int ArchiveMaxRetryCount { get; set; } = 8;
    public int[] ArchiveRetryScheduleSeconds { get; set; } = [60, 300, 900, 3600];
}

public sealed class KmIntegrationAuthOptions
{
    public const string SectionName = "KmIntegrationAuth";
    public string? ClientId { get; set; }
    public string? Audience { get; set; }
    public string? DeletionStatusScope { get; set; }
}

public sealed class KmIntegrationM2MOptions
{
    public const string SectionName = "KmIntegrationM2M";
    public string? Authority { get; set; }
    public string? ClientId { get; set; }
    public string? TokenEndpoint { get; set; }
    public string? Audience { get; set; }
    public string? Scope { get; set; }
    public string? ClientSecretFile { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Authority) &&
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(TokenEndpoint) &&
        !string.IsNullOrWhiteSpace(Audience) &&
        !string.IsNullOrWhiteSpace(Scope) &&
        !string.IsNullOrWhiteSpace(ClientSecretFile);
}
