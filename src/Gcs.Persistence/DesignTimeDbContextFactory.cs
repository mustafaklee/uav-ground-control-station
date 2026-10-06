using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Gcs.Persistence;

/// <summary>
/// Lets <c>dotnet ef</c> (migrations add, migrations bundle) create the context without starting the API.
/// The connection string is only used by commands that touch a database; the migrator bundle overrides it
/// with <c>--connection</c> at run time.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<GcsDbContext>
{
    private const string ConnectionVariable = "GCS_DESIGN_TIME_CONNECTION";
    private const string LocalDevelopmentConnection = "Host=localhost;Port=5432;Database=gcs;Username=gcs;Password=gcs_dev_password";
    private const int CommandTimeoutSeconds = 300;
    private const int MaxRetryCount = 3;

    public GcsDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionVariable) ?? LocalDevelopmentConnection;
        var options = new DbContextOptionsBuilder<GcsDbContext>();
        DependencyInjection.Configure(options, connectionString, CommandTimeoutSeconds, MaxRetryCount);
        return new GcsDbContext(options.Options);
    }
}
