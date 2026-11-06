using DotNet.Testcontainers.Configurations;
using MedulaPrecheck.Oracle;
using MedulaPrecheck.Sut;
using Oracle.ManagedDataAccess.Client;
using Testcontainers.Oracle;

[assembly: AssemblyFixture(typeof(MedulaPrecheck.OracleTests.OracleFixture))]

namespace MedulaPrecheck.OracleTests;

/// <summary>
/// One Oracle Database 23ai Free container per test run. Users come from the same init script docker compose uses;
/// the schema is installed and the reference data loaded as PRECHECK_OWNER; tests run the bulk path as PRECHECK_APP.
/// </summary>
public sealed class OracleFixture : IAsyncLifetime
{
    // Test-only credentials for a throwaway container.
    private const string OwnerPassword = "OwnerTest1";
    private const string AppPassword = "AppTest1";

    private readonly OracleContainer _container = new OracleBuilder("gvenzl/oracle-free:23-slim-faststart")
        .WithEnvironment("OWNER_PASSWORD", OwnerPassword)
        .WithEnvironment("APP_PASSWORD", AppPassword)
        .WithResourceMapping(
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "oracle-init", "01-users.sh")),
            "/container-entrypoint-initdb.d/01-users.sh",
            fileMode: UnixFileModes.UserRead | UnixFileModes.UserWrite | UnixFileModes.UserExecute | UnixFileModes.GroupRead | UnixFileModes.OtherRead)
        .WithLabel("project", "medula-precheck-tests")
        .Build();

    public string OwnerConnectionString => WithCredentials("precheck_owner", OwnerPassword);

    public string AppConnectionString => WithCredentials("precheck_app", AppPassword);

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        var installer = new OracleSchemaInstaller(OwnerConnectionString);
        await installer.InstallAsync("precheck_app");
        await installer.LoadReferenceDataAsync(SutCatalog.Default);
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    private string WithCredentials(string user, string password) =>
        new OracleConnectionStringBuilder(_container.GetConnectionString())
        {
            UserID = user,
            Password = password,
        }.ConnectionString;
}
