using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
            // Mirrors Program.cs's registration for PseudonymContext: a factory as the sole
            // source of configuration, with a scoped registration deriving one instance per
            // scope from it - not a separate AddDbContext call, which would conflict on
            // DbContextOptions<TDbContext>'s lifetime. Not pooled: PseudonymContext's
            // OnConfiguring mutates options post-construction, which pooling disallows.
            services.AddDbContextFactory<TDbContext>(options => options.UseSqlite(inMemorySqlite));
            services.AddScoped<TDbContext>(isp =>
                isp.GetRequiredService<IDbContextFactory<TDbContext>>().CreateDbContext()
            );
            services.EnsureDbCreated<TDbContext>();
            services.RemovePseudonymCountMetricsPublisher();
        });

        builder.UseEnvironment("Test");
    }
}

internal static class ServiceCollectionExtensions
{
    public static void RemoveDbContext<T>(this IServiceCollection services)
        where T : DbContext
    {
        var descriptor = services.SingleOrDefault(d =>
            d.ServiceType == typeof(IDbContextOptionsConfiguration<T>)
        );
        if (descriptor != null)
            services.Remove(descriptor);
    }

    /// <summary>
    /// Stops a test host from publishing the pseudonym-count gauge. PseudonymCountMetrics keeps its
    /// latest counts in a process-wide static that PublishFromSnapshotAsync replaces wholesale, and
    /// this background service publishes once immediately on startup - so every host booted in the
    /// test process overwrites whatever PseudonymCountMetricsTests had just published, from its own
    /// empty in-memory database. Booting a host is lazy (it happens on the first CreateClient or
    /// Services access), which is what made that collision intermittent rather than constant.
    /// Nothing under test here reads the gauge, so the publisher simply does not belong in these
    /// hosts.
    /// </summary>
    public static void RemovePseudonymCountMetricsPublisher(this IServiceCollection services)
    {
        var descriptor = services.FirstOrDefault(d =>
            d.ServiceType == typeof(IHostedService)
            && d.ImplementationType == typeof(PseudonymCountMetricsBackgroundService)
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
