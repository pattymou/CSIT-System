using System.ComponentModel.DataAnnotations.Schema;

namespace SIT.DepartmentSystem.Web.Entities;

[Table("system_options")]
public class SystemOption
{
    [Column("id")]
    public Guid Id { get; set; }

    [Column("category")]
    public string Category { get; set; } = string.Empty;

    [Column("name")]
    public string Name { get; set; } = string.Empty;

    [Column("value")]
    public string Value { get; set; } = string.Empty;

    [Column("sort")]
    public int Sort { get; set; }

    [Column("is_enabled")]
    public bool IsEnabled { get; set; } = true;

    [Column("note")]
    public string? Note { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}

public static class SystemOptionCategories
{
    public const string Team = "Team";
    public const string Reservation = "Reservation";
    public const string Customer = "Customer";
    public const string SubPu = "SubPu";
}
