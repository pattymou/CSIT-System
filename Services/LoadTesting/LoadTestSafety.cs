using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;

namespace SIT.DepartmentSystem.Web.Services.LoadTesting;

public static class LoadTestSafety
{
    private static readonly string[] LoopbackHosts = ["127.0.0.1", "localhost", "::1"];

    public static void ValidateBeforeServices(WebApplicationBuilder builder)
    {
        if (!builder.Environment.IsEnvironment("LoadTest")) return;
        var config = builder.Configuration;
        if (!string.Equals(config["LoadTest:Required"], "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("LoadTest:Required=true is mandatory.");
        var runId = Required(config["LoadTest:RunId"], "LoadTest:RunId");
        if (!System.Text.RegularExpressions.Regex.IsMatch(runId, "^[a-z0-9]{6,32}$"))
            throw new InvalidOperationException("LoadTest:RunId must be a short lowercase synthetic identifier.");

        var urls = Required(config["ASPNETCORE_URLS"], "ASPNETCORE_URLS")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (urls.Length != 1 || !Uri.TryCreate(urls[0], UriKind.Absolute, out var target) ||
            target.Scheme != Uri.UriSchemeHttp || !LoopbackHosts.Contains(target.Host, StringComparer.OrdinalIgnoreCase) ||
            target.Port is 5010 or 7443)
            throw new InvalidOperationException("The load-test host must use one loopback HTTP URL and must not use ports 5010 or 7443.");

        var connection = new Npgsql.NpgsqlConnectionStringBuilder(
            Required(config.GetConnectionString("Postgres"), "ConnectionStrings:Postgres"));
        var expectedDatabase = $"csit_load_test_{runId}";
        if (!LoopbackHosts.Contains(connection.Host, StringComparer.OrdinalIgnoreCase) ||
            !string.Equals(connection.Database, expectedDatabase, StringComparison.Ordinal) ||
            !string.Equals(connection.Username, "postgres", StringComparison.Ordinal))
            throw new InvalidOperationException("The PostgreSQL target is not the run-scoped loopback load-test database.");

        var allowedRoot = FullPath(Required(config["LoadTest:AllowedRoot"], "LoadTest:AllowedRoot"));
        var storageRoot = FullPath(Required(config["LoadTest:StorageRoot"], "LoadTest:StorageRoot"));
        if (new Uri(storageRoot).IsUnc || !IsChild(storageRoot, allowedRoot) ||
            !File.Exists(Path.Combine(storageRoot, $".csit-load-test-{runId}")))
            throw new InvalidOperationException("The isolated storage root or its run marker is invalid.");
        foreach (var path in new[] { config["DocumentStorage:RootPath"], config["DocumentStaging:RootPath"],
                     config["RawData:NasRootPath"] }.Where(x => !string.IsNullOrWhiteSpace(x)))
            if (!IsChild(FullPath(path!), storageRoot))
                throw new InvalidOperationException("Every configured file root must remain under the isolated storage root.");

        foreach (var key in new[] { "KmIntegration:Enabled", "KmIntegration:VendorEnabled",
                     "KmIntegration:EquipmentEnabled", "KmDeletion:Enabled", "KmDeletion:VendorEnabled",
                     "KmDeletion:EquipmentEnabled" })
            if (config.GetValue<bool>(key)) throw new InvalidOperationException($"{key} must remain false during load tests.");
        if (!string.Equals(config["AuthenticationMode"] ?? "Legacy", "Legacy", StringComparison.Ordinal))
            throw new InvalidOperationException("Load tests cannot use Shared Identity/OIDC.");
    }

    public static async Task ValidateDatabaseAsync(IServiceProvider services, IConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var runId = Required(config["LoadTest:RunId"], "LoadTest:RunId");
        var expectedCluster = Required(config["LoadTest:ExpectedClusterSystemId"], "LoadTest:ExpectedClusterSystemId");
        await using var scope = services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        var database = await ScalarAsync(connection, "SELECT current_database()", cancellationToken);
        var cluster = await ScalarAsync(connection, "SELECT system_identifier::text FROM pg_control_system()", cancellationToken);
        var major = await ScalarAsync(connection, "SELECT (current_setting('server_version_num')::int / 10000)::text", cancellationToken);
        var inventory = await ListAsync(connection, "SELECT datname FROM pg_database ORDER BY datname", cancellationToken);
        if (database != $"csit_load_test_{runId}" || cluster != expectedCluster || major != "16")
            throw new InvalidOperationException("PostgreSQL runtime identity does not match the verified PostgreSQL 16 test cluster.");
        var forbidden = inventory.FirstOrDefault(x => x.Equals("sit_department_system", StringComparison.OrdinalIgnoreCase) ||
            System.Text.RegularExpressions.Regex.IsMatch(x, "(^|_)(dev|prod|production)(_|$)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        if (forbidden is not null)
            throw new InvalidOperationException($"Refusing a cluster containing a DEV/Production-like database: {forbidden}.");
    }

    public static void DemandRequest(HttpContext context, IConfiguration configuration)
    {
        var address = context.Connection.RemoteIpAddress;
        var loopback = address is not null && System.Net.IPAddress.IsLoopback(address);
        if (!context.RequestServices.GetRequiredService<IWebHostEnvironment>().IsEnvironment("LoadTest") || !loopback ||
            context.User.FindFirst("load_test_run_id")?.Value != configuration["LoadTest:RunId"])
            throw new UnauthorizedAccessException("The load-test control plane is unavailable.");
    }

    private static async Task<string> ScalarAsync(DbConnection connection, string sql, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(token)) ?? string.Empty;
    }
    private static async Task<List<string>> ListAsync(DbConnection connection, string sql, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(token); var values = new List<string>();
        while (await reader.ReadAsync(token)) values.Add(reader.GetString(0)); return values;
    }
    private static string Required(string? value, string name) => string.IsNullOrWhiteSpace(value)
        ? throw new InvalidOperationException($"{name} is required for LoadTest.") : value.Trim();
    private static string FullPath(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    private static bool IsChild(string path, string root) => path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
}
