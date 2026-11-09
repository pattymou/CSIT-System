using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using SIT.DepartmentSystem.Web.Services.LoadTesting;

namespace ApprovalFramework.Tests;

public sealed class LoadTestSafetyTests
{
    [Fact]
    public void ValidIsolatedConfiguration_IsAccepted()
    {
        using var setup = SafetySetup.Create();
        LoadTestSafety.ValidateBeforeServices(setup.Builder);
    }

    [Fact]
    public void UnsafeTargetsAndDispatchSettings_AreRejected()
    {
        var mutations = new Action<IDictionary<string, string?>>[]
        {
            values => values["ASPNETCORE_URLS"] = "https://127.0.0.1:7443",
            values => values["ConnectionStrings:Postgres"] =
                "Host=dev-db.example;Port=5432;Database=csit_load_test_safety01;Username=postgres;Password=x",
            values => values["ConnectionStrings:Postgres"] =
                "Host=127.0.0.1;Port=5432;Database=sit_department_system;Username=postgres;Password=x",
            values => values["DocumentStorage:RootPath"] = Path.GetPathRoot(Path.GetTempPath())!,
            values => values["KmIntegration:Enabled"] = "true",
            values => values["AuthenticationMode"] = "SharedIdentity"
        };

        foreach (var mutate in mutations)
        {
            using var setup = SafetySetup.Create(mutate);
            Assert.Throws<InvalidOperationException>(() =>
                LoadTestSafety.ValidateBeforeServices(setup.Builder));
        }
    }

    [Fact]
    public void NonLoadTestEnvironment_NeverEnablesLoadTestValidationPath()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production"
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LoadTest:Required"] = "true",
            ["ASPNETCORE_URLS"] = "http://0.0.0.0:5010"
        });

        LoadTestSafety.ValidateBeforeServices(builder);
    }

    private sealed class SafetySetup : IDisposable
    {
        private SafetySetup(string root, WebApplicationBuilder builder)
        {
            Root = root;
            Builder = builder;
        }

        public string Root { get; }
        public WebApplicationBuilder Builder { get; }

        public static SafetySetup Create(Action<IDictionary<string, string?>>? mutate = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "csit-load-safety-tests", Guid.NewGuid().ToString("N"));
            var storage = Path.Combine(root, "storage");
            Directory.CreateDirectory(storage);
            File.WriteAllText(Path.Combine(storage, ".csit-load-test-safety01"), "test");
            var values = new Dictionary<string, string?>
            {
                ["LoadTest:Required"] = "true",
                ["LoadTest:RunId"] = "safety01",
                ["LoadTest:AllowedRoot"] = root,
                ["LoadTest:StorageRoot"] = storage,
                ["ASPNETCORE_URLS"] = "http://127.0.0.1:5088",
                ["ConnectionStrings:Postgres"] =
                    "Host=127.0.0.1;Port=5432;Database=csit_load_test_safety01;Username=postgres;Password=x",
                ["DocumentStorage:RootPath"] = Path.Combine(storage, "documents"),
                ["DocumentStaging:RootPath"] = Path.Combine(storage, "staging"),
                ["RawData:NasRootPath"] = Path.Combine(storage, "raw"),
                ["AuthenticationMode"] = "Legacy"
            };
            mutate?.Invoke(values);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "LoadTest"
            });
            builder.Configuration.AddInMemoryCollection(values);
            return new SafetySetup(root, builder);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
