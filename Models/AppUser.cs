namespace SIT.DepartmentSystem.Web.Models;

public class AppUser
{
    public Guid Id { get; set; }
    public string Account { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool IsAdmin { get; set; }
}