using Microsoft.AspNetCore.Authorization;

namespace SIT.DepartmentSystem.Web.Services;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class BusinessWriteAttribute : AuthorizeAttribute
{
    public BusinessWriteAttribute() => Policy = SystemAuthorization.Policies.BusinessWrite;
}
