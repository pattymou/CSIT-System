using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SIT.DepartmentSystem.Web.Services;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Route("api/manual/assets")]
public sealed partial class ManualAssetsController(IWebHostEnvironment environment) : ControllerBase
{
    [HttpGet("shared/{name}")]
    [Authorize]
    public IActionResult Shared(string name) => ReadImage("shared", name);

    [HttpGet("internal/{name}")]
    [Authorize(Policy = SystemAuthorization.Policies.CsitStaff)]
    public IActionResult Internal(string name) => ReadImage("internal", name);

    [HttpGet("rd/{name}")]
    [Authorize(Policy = SystemAuthorization.Policies.RdApplicant)]
    public IActionResult Rd(string name) => ReadImage("rd", name);

    private IActionResult ReadImage(string audience, string name)
    {
        if (!SafeName().IsMatch(name)) return NotFound();
        var path = Path.Combine(environment.ContentRootPath, "ManualAssets", audience, name);
        if (!System.IO.File.Exists(path)) return NotFound();
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        return PhysicalFile(path, "image/png");
    }

    [GeneratedRegex("^[a-z0-9-]+\\.png$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeName();
}
