using System.Security.Claims;
using SIT.DepartmentSystem.Web.Models.Api;

namespace SIT.DepartmentSystem.Web.Services.Interfaces;

public interface IResourceSchedulerService
{
    Task<ResourceAssignmentProposal> ProposeAsync(
        ClaimsPrincipal user,
        ResourceSchedulerProposalRequest request,
        CancellationToken cancellationToken = default);
}
