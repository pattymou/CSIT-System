namespace SIT.DepartmentSystem.Web.Services.Approval;

public sealed class ApprovalCommitHandlerRegistry : IApprovalCommitHandlerRegistry
{
    private readonly Dictionary<string, IApprovalCommitHandler> _handlers;

    public ApprovalCommitHandlerRegistry(IEnumerable<IApprovalCommitHandler> handlers)
    {
        _handlers = new Dictionary<string, IApprovalCommitHandler>(StringComparer.OrdinalIgnoreCase);
        foreach (var handler in handlers)
        {
            if (string.IsNullOrWhiteSpace(handler.ModuleCode) || !_handlers.TryAdd(handler.ModuleCode.Trim(), handler))
                throw new InvalidOperationException("Approval commit handler ModuleCode is blank or duplicated.");
        }
    }

    public bool HasAnyHandler => _handlers.Count > 0;
    public bool HasHandler(string? moduleCode) =>
        !string.IsNullOrWhiteSpace(moduleCode) && _handlers.ContainsKey(moduleCode.Trim());

    public IApprovalCommitHandler Resolve(string? moduleCode) =>
        HasHandler(moduleCode)
            ? _handlers[moduleCode!.Trim()]
            : throw new InvalidOperationException("此 Module 尚無可用的主管審核提交處理器。");
}
