using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Vfps.Tests.WebAppTests;

// via https://www.azureblue.io/asp-net-core-integration-tests-with-test-containers-and-postgres/
[ExcludeFromCodeCoverage]
public class IntegrationTestFactory<TProgram, TDbContext> : WebApplicationFactory<TProgram>
    where TProgram : class
    where TDbContext : DbContext
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            var inMemorySqlite = new SqliteConnection("Data Source=:memory:");
            inMemorySqlite.Open();

            services.RemoveDbContext<TDbContext>();
            services.AddDbContext<TDbContext>(options => options.UseSqlite(inMemorySqlite));
            services.EnsureDbCreated<TDbContext>();
        });

        builder.UseEnvironment("Test");
    }
}

internal static class ServiceCollectionExtensions
{
    public static void RemoveDbContext<T>(this IServiceCollection services)
        where T : DbContext
    {
        var descriptor = services.SingleOrDefault(
            d => d.ServiceType == typeof(DbContextOptions<T>)
        );
        if (descriptor != null)
            services.Remove(descriptor);
    }

    public static void EnsureDbCreated<T>(this IServiceCollection services)
        where T : DbContext
    {
        var serviceProvider = services.BuildServiceProvider();

        using var scope = serviceProvider.CreateScope();
        var scopedServices = scope.ServiceProvider;
        var context = scopedServices.GetRequiredService<T>();
        context.Database.EnsureCreated();
    }
}
