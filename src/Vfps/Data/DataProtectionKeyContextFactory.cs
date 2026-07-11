using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Vfps.Data;

/// <summary>
/// Lets `dotnet ef` construct <see cref="DataProtectionKeyContext"/> at design time. The
/// context is only registered in the app's DI container when Authorization:IsEnabled is set
/// (see Program.cs), so `dotnet ef` - which builds the app host to discover DbContexts - can't
/// find it under default config. This factory bypasses that by reading the connection string
/// directly, independent of the conditional runtime registration.
/// </summary>
public class DataProtectionKeyContextFactory : IDesignTimeDbContextFactory<DataProtectionKeyContext>
{
    public DataProtectionKeyContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile(
                $"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")}.json",
                optional: true
            )
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString("PostgreSQL");

        var optionsBuilder = new DbContextOptionsBuilder<DataProtectionKeyContext>();
        optionsBuilder.UseNpgsql(connectionString);

        return new DataProtectionKeyContext(optionsBuilder.Options);
    }
}
