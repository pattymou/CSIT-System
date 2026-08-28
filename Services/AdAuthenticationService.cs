using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Models;
using System.DirectoryServices.AccountManagement;
using System.Net.Mime;
using System.Security.Claims;

namespace SIT.DepartmentSystem.Web.Services;

public class AdAuthenticationService(IConfiguration configuration, IHostEnvironment environment, AppDbContext db)
{
    public async Task<ClaimsPrincipal?> AuthenticateAsync(string username, string password)
    {
        var domain = configuration["Ad:Domain"];
        var container = configuration["Ad:Container"];
        var developmentUser = ValidateDevelopmentCredentials(username, password);
        var authenticatedByDevAuth = developmentUser is not null;
        var ok = authenticatedByDevAuth;

        if (!authenticatedByDevAuth)
        {
            try
            {
                using var context = string.IsNullOrWhiteSpace(container)
                    ? new PrincipalContext(ContextType.Domain, domain)
                    : new PrincipalContext(ContextType.Domain, domain, container);

                ok = context.ValidateCredentials(username, password);
            }
            catch
            {
                ok = false;
            }
        }

        if (!ok) return null;

        var account = developmentUser?.Username ?? username.ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(x => x.Account == account);

        if (user is null)
        {
            user = new AppUser
            {
                Account = account,
                DisplayName = developmentUser?.DisplayName ?? username,
                Department = developmentUser?.Department ?? "DA40",
                Email = developmentUser?.Email ?? $"{account}@example.com",
                IsAdmin = developmentUser?.IsAdmin ?? false
            };

            db.Users.Add(user);
            await db.SaveChangesAsync();
        }
        else if (developmentUser is not null)
        {
            user.DisplayName = developmentUser.DisplayName;
            user.Department = developmentUser.Department;
            user.Email = developmentUser.Email;
            user.IsAdmin = developmentUser.IsAdmin;
            await db.SaveChangesAsync();
        }

        var accessScope = ResolveAccessScope(developmentUser, user);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Account),
            new(ClaimTypes.Name, user.DisplayName),
            new(ClaimTypes.Role, user.IsAdmin ? "Admin" : "User"),
            new("account", user.Account),
            new("department", user.Department)
        };
        if (accessScope is not null)
        {
            claims.Add(new Claim(SystemAuthorization.AccessScopeClaim, accessScope));
        }

        return new ClaimsPrincipal(
            new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)
        );
    }

    private DevelopmentUser? ValidateDevelopmentCredentials(string username, string password)
    {
        if (!environment.IsDevelopment())
        {
            return null;
        }

        var configuredUsers = configuration
            .GetSection("DevAuth:Users")
            .GetChildren()
            .Select(section => new DevelopmentUser
            {
                Username = section["Username"] ?? string.Empty,
                Password = section["Password"] ?? string.Empty,
                DisplayName = section["DisplayName"] ?? string.Empty,
                Email = section["Email"] ?? string.Empty,
                Department = section["Department"] ?? string.Empty,
                IsAdmin = bool.TryParse(section["IsAdmin"], out var isAdmin) && isAdmin,
                AccessScope = section["AccessScope"] ?? string.Empty
            });

        foreach (var configuredUser in configuredUsers)
        {
            if (string.IsNullOrWhiteSpace(configuredUser.Username)
                || string.IsNullOrEmpty(configuredUser.Password)
                || !username.Equals(configuredUser.Username, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(password, configuredUser.Password, StringComparison.Ordinal))
            {
                continue;
            }

            var account = configuredUser.Username.Trim().ToLowerInvariant();
            return new DevelopmentUser
            {
                Username = account,
                Password = configuredUser.Password,
                DisplayName = ValueOrDefault(configuredUser.DisplayName, account),
                Email = ValueOrDefault(configuredUser.Email, $"{account}@dev.local"),
                Department = ValueOrDefault(configuredUser.Department, "Development"),
                IsAdmin = configuredUser.IsAdmin,
                AccessScope = configuredUser.AccessScope
            };
        }

        var devUsername = configuration["DevAuth:Username"];
        var devPassword = configuration["DevAuth:Password"];

        if (string.IsNullOrWhiteSpace(devUsername) || string.IsNullOrEmpty(devPassword))
        {
            return null;
        }

        if (!username.Equals(devUsername, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(password, devPassword, StringComparison.Ordinal))
        {
            return null;
        }

        var legacyAccount = devUsername.Trim().ToLowerInvariant();
        return new DevelopmentUser
        {
            Username = legacyAccount,
            Password = devPassword,
            DisplayName = "Development Admin",
            Email = $"{legacyAccount}@dev.local",
            Department = "CSIT",
            IsAdmin = true,
            AccessScope = SystemAuthorization.AccessScopes.CsitStaff
        };
    }

    private static string? ResolveAccessScope(DevelopmentUser? developmentUser, AppUser user)
    {
        if (user.IsAdmin)
        {
            return SystemAuthorization.AccessScopes.CsitStaff;
        }

        if (developmentUser is null)
        {
            // Production currently has no trustworthy AppUser field or AD-group mapping
            // that distinguishes CSIT Staff from RD Applicants. Fail closed.
            return null;
        }

        var accessScope = developmentUser.AccessScope.Trim();
        if (accessScope.Equals(SystemAuthorization.AccessScopes.RdApplicant, StringComparison.OrdinalIgnoreCase))
        {
            return SystemAuthorization.AccessScopes.RdApplicant;
        }

        if (accessScope.Equals(SystemAuthorization.AccessScopes.CsitStaff, StringComparison.OrdinalIgnoreCase))
        {
            return SystemAuthorization.AccessScopes.CsitStaff;
        }

        throw new InvalidOperationException(
            $"DevAuth AccessScope for '{developmentUser.Username}' must be RdApplicant or CsitStaff.");
    }

    private static string ValueOrDefault(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private sealed class DevelopmentUser
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Department { get; set; } = string.Empty;
        public bool IsAdmin { get; set; }
        public string AccessScope { get; set; } = string.Empty;
    }
}
