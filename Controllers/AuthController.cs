using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SIT.DepartmentSystem.Web.Services;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
public class AuthController(AdAuthenticationService ad) : Controller
{
    [HttpGet("/")]
    [AllowAnonymous]
    public IActionResult Root() => Redirect("/signin");

    [HttpGet("/signin")]
    [AllowAnonymous]
    public ContentResult LoginPage([FromQuery] int? error = null, [FromQuery] string? returnUrl = null)
    {
        var safeReturnUrl = GetSafeReturnUrl(returnUrl);
        var hasError = error == 1;
        var errorHtml = hasError
            ? "<div class='error-text'>登入失敗，請確認 AD 帳密。</div>"
            : string.Empty;
        var returnUrlInput = safeReturnUrl is null
            ? string.Empty
            : $"<input type='hidden' name='returnUrl' value='{WebUtility.HtmlEncode(safeReturnUrl)}' />";

        var html = $$"""
<!DOCTYPE html>
<html lang="zh-Hant">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>SIT System Login</title>
  <style>
    :root {
      color-scheme: dark;
      --bg:#091018;
      --bg2:#0b1220;
      --panel:rgba(18,27,38,.96);
      --border:rgba(148,163,184,.18);
      --text:#e8edf7;
      --muted:#94a3b8;
      --accent:#0ea5a4;
      --accent2:#38bdf8;
      --danger:#fca5a5;
      --shadow:0 24px 60px rgba(2,6,23,.45);
    }
    * { box-sizing:border-box; }
    html, body { margin:0; min-height:100%; font-family:Inter,"Segoe UI",sans-serif; background:
      radial-gradient(circle at top, rgba(56,189,248,.10), transparent 24%),
      linear-gradient(180deg, var(--bg), var(--bg2) 55%, #0a1018);
      color:var(--text); }
    body { min-height:100vh; }
    .shell {
      min-height:100vh;
      display:flex;
      align-items:center;
      justify-content:center;
      padding:24px 16px;
    }
    .card {
      width:min(100%, 440px);
      background:linear-gradient(180deg, var(--panel), rgba(11,17,29,.96));
      border:1px solid var(--border);
      border-radius:24px;
      padding:28px;
      box-shadow:var(--shadow);
    }
    h1 { margin:0 0 10px; font-size:clamp(1.8rem, 3vw, 2.4rem); line-height:1.1; }
    p { margin:0 0 18px; color:var(--muted); }
    input {
      width:100%; margin:0 0 14px; padding:15px 16px; border-radius:14px;
      border:1px solid rgba(148,163,184,.16); background:rgba(7,12,22,.92);
      color:var(--text); font-size:16px;
    }
    input:focus { outline:none; border-color:rgba(56,189,248,.55); box-shadow:0 0 0 4px rgba(15,118,110,.14); }
    button {
      width:100%; border:none; border-radius:14px; padding:15px 16px; cursor:pointer;
      font-weight:800; font-size:1rem; color:#f8fafc;
      background:linear-gradient(135deg, #0f766e, var(--accent));
      box-shadow:0 12px 24px rgba(15,118,110,.24);
    }
    .hint-text { margin-top:14px; color:var(--muted); font-size:.9rem; }
    .error-text { margin:0 0 14px; color:var(--danger); font-size:.95rem; }
    @media (max-width: 640px) {
      .shell { padding:16px 12px; align-items:flex-start; }
      .card { padding:22px 18px; border-radius:20px; }
      h1 { margin-bottom:8px; }
    }
  </style>
</head>
<body>
  <div class="shell">
    <form class="card" method="post" action="/auth/login">
      {{returnUrlInput}}
      <h1>SIT System</h1>
      <p>使用公司 AD 帳號登入</p>
      {{errorHtml}}
      <input name="username" placeholder="AD 帳號" autocomplete="username" />
      <input name="password" type="password" placeholder="密碼" autocomplete="current-password" />
      <button type="submit">登入</button>
    </form>
  </div>
</body>
</html>
""";

        return Content(html, "text/html; charset=utf-8");
    }

    [HttpPost("/auth/login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromForm] string username, [FromForm] string password, [FromForm] string? returnUrl = null)
    {
        var safeReturnUrl = GetSafeReturnUrl(returnUrl);
        var principal = await ad.AuthenticateAsync(username, password);
        if (principal is null)
        {
            var failureUrl = "/signin?error=1";
            if (safeReturnUrl is not null)
            {
                failureUrl += $"&returnUrl={Uri.EscapeDataString(safeReturnUrl)}";
            }

            return Redirect(failureUrl);
        }

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        return Redirect(safeReturnUrl ?? "/home");
    }

    [HttpGet("/auth/logout")]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Redirect("/signin");
    }

    private string? GetSafeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl) || !Url.IsLocalUrl(returnUrl))
        {
            return null;
        }

        return returnUrl.StartsWith("/signin", StringComparison.OrdinalIgnoreCase) ? null : returnUrl;
    }
}
