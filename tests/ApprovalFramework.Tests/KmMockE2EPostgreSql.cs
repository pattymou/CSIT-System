using Npgsql;

namespace ApprovalFramework.Tests;

internal static class KmMockE2EPostgreSql
{
    private const string RequiredVariable = "CSIT_KM_MOCK_E2E_REQUIRED";
    private const string ClusterIdVariable = "CSIT_KM_MOCK_E2E_CLUSTER_ID";

    public static bool TryGetConnection(
        string variable,
        IReadOnlyCollection<string> legacyDatabasePrefixes,
        IReadOnlyCollection<int> legacyPorts,
        out string connection)
    {
        connection = Environment.GetEnvironmentVariable(variable) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(connection))
        {
            Assert.False(IsRequired(),
                $"{variable} is required when {RequiredVariable}=true. PostgreSQL coverage must not be reported as passed without executing.");
            return false;
        }

        var info = new NpgsqlConnectionStringBuilder(connection);
        Assert.Contains(info.Host, new[] { "127.0.0.1", "localhost" });
        Assert.False(string.IsNullOrWhiteSpace(info.Database));

        if (IsRequired())
        {
            var runId = Environment.GetEnvironmentVariable("CSIT_KM_MOCK_E2E_RUN_ID");
            Assert.False(string.IsNullOrWhiteSpace(runId),
                "CSIT_KM_MOCK_E2E_RUN_ID must identify the isolated CI database.");
            Assert.StartsWith("csit_km_mock_e2e_", info.Database, StringComparison.Ordinal);
            Assert.Contains(runId!, info.Database, StringComparison.Ordinal);
            Assert.Equal("postgres", info.Username);
            Assert.DoesNotContain("dev", info.Database, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("prod", info.Database, StringComparison.OrdinalIgnoreCase);
            AssertConfirmedCluster(info, runId!);
        }
        else
        {
            Assert.Contains(info.Port, legacyPorts);
            Assert.Contains(legacyDatabasePrefixes,
                prefix => info.Database!.StartsWith(prefix, StringComparison.Ordinal));
        }

        return true;
    }

    private static void AssertConfirmedCluster(NpgsqlConnectionStringBuilder info, string runId)
    {
        var expectedClusterId = Environment.GetEnvironmentVariable(ClusterIdVariable);
        Assert.False(string.IsNullOrWhiteSpace(expectedClusterId),
            $"{ClusterIdVariable} must identify the independently verified PostgreSQL cluster.");

        using var database = new NpgsqlConnection(info.ConnectionString);
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = """
            SELECT current_database(), system_identifier::text, inet_server_addr()::text,
                   inet_server_port(), current_setting('server_version_num')::int / 10000
            FROM pg_control_system();
            """;
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read(), "PostgreSQL cluster identity query returned no row.");
        Assert.Equal(info.Database, reader.GetString(0));
        Assert.Equal(expectedClusterId, reader.GetString(1));
        var serverAddress = reader.GetString(2);
        Assert.True(serverAddress is "127.0.0.1" or "127.0.0.1/32" or "::1" or "::1/128",
            $"PostgreSQL server address must be loopback-only, received '{serverAddress}'.");
        Assert.Equal(info.Port, reader.GetInt32(3));
        Assert.Equal(16, reader.GetInt32(4));
        reader.Close();

        using var inventory = database.CreateCommand();
        inventory.CommandText = """
            SELECT datname FROM pg_database
            WHERE datname ~* '(^|_)(dev|prod|production)(_|$)'
               OR datname = 'sit_department_system'
            LIMIT 1;
            """;
        var forbidden = inventory.ExecuteScalar() as string;
        Assert.True(string.IsNullOrWhiteSpace(forbidden),
            $"Refusing PostgreSQL cluster containing DEV/Production-like database '{forbidden}'. Run {runId} is not isolated.");
    }

    private static bool IsRequired() =>
        string.Equals(Environment.GetEnvironmentVariable(RequiredVariable), "true",
            StringComparison.OrdinalIgnoreCase);
}
