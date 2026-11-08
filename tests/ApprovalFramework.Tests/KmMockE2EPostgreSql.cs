using Npgsql;

namespace ApprovalFramework.Tests;

internal static class KmMockE2EPostgreSql
{
    private const string RequiredVariable = "CSIT_KM_MOCK_E2E_REQUIRED";

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
        }
        else
        {
            Assert.Contains(info.Port, legacyPorts);
            Assert.Contains(legacyDatabasePrefixes,
                prefix => info.Database!.StartsWith(prefix, StringComparison.Ordinal));
        }

        return true;
    }

    private static bool IsRequired() =>
        string.Equals(Environment.GetEnvironmentVariable(RequiredVariable), "true",
            StringComparison.OrdinalIgnoreCase);
}
