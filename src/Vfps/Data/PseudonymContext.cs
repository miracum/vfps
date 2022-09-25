using EntityFramework.Exceptions.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Vfps.Data.Models;

namespace Vfps.Data;

public class PseudonymContext : DbContext
{
    public PseudonymContext(DbContextOptions<PseudonymContext> options) : base(options)
    {
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseExceptionProcessor();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Pseudonym>()
            .HasKey(c => new { c.NamespaceName, c.OriginalValue });
    }

    public DbSet<Pseudonym> Pseudonyms { get; set; }
    public DbSet<Namespace> Namespaces { get; set; }
}
