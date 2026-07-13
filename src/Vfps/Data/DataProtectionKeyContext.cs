using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Vfps.Data;

/// <summary>
/// Dedicated context for the ASP.NET Core Data Protection key ring, kept separate from
/// <see cref="PseudonymContext"/> so this infrastructure-level table's migrations don't get
/// mixed into the domain model's snapshot. Points at the same Postgres database/connection
/// string as <see cref="PseudonymContext"/> - this is one extra DbContext, not a new service.
/// </summary>
public class DataProtectionKeyContext(DbContextOptions<DataProtectionKeyContext> options)
    : DbContext(options),
        IDataProtectionKeyContext
{
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSnakeCaseNamingConvention();
    }

    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; } = null!;
}
