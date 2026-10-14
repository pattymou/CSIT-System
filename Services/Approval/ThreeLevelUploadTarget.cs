using System.Text;

namespace SIT.DepartmentSystem.Web.Services.Approval;

public enum ThreeLevelUploadKind
{
    CaseAttachment,
    CaseAttachmentByNo,
    TaskAttachment,
    TaskAttachmentByNo,
    TestReport,
    TestReportByTaskNo,
    TestReportVersion
}

/// <summary>The route's trusted domain identifier, not a client supplied menu or module code.</summary>
public sealed record ThreeLevelUploadTarget(ThreeLevelUploadKind Kind, Guid Id, string? Number = null)
{
    public string ContextType => Kind.ToString();

    public string ContextId
    {
        get
        {
            if (Id == Guid.Empty) throw new InvalidOperationException("Upload resource ID is missing.");
            if (Number is null) return Id.ToString("N");
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(Number))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var value = $"{Id:N}:{encoded}";
            if (value.Length > 150) throw new InvalidOperationException("Case/Task number is too long for approval context.");
            return value;
        }
    }

    public static ThreeLevelUploadTarget FromContext(string type, string id)
    {
        if (!Enum.TryParse<ThreeLevelUploadKind>(type, out var kind) || !Enum.IsDefined(kind))
            throw new InvalidOperationException("Unknown ThreeLevel approval context.");
        var parts = id.Split(':', 2);
        if (!Guid.TryParseExact(parts[0], "N", out var resourceId))
            throw new InvalidOperationException("Invalid ThreeLevel approval resource.");
        var requiresNumber = kind is ThreeLevelUploadKind.CaseAttachmentByNo
            or ThreeLevelUploadKind.TaskAttachmentByNo or ThreeLevelUploadKind.TestReportByTaskNo;
        if (requiresNumber != (parts.Length == 2))
            throw new InvalidOperationException("Invalid ThreeLevel approval context number.");
        if (!requiresNumber) return new(kind, resourceId);
        var encoded = parts[1].Replace('-', '+').Replace('_', '/');
        encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
        var number = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        if (string.IsNullOrWhiteSpace(number)) throw new InvalidOperationException("Case/Task number is missing.");
        return new(kind, resourceId, number);
    }
}
