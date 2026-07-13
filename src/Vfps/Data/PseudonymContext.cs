using System.Text.Json;
using EntityFramework.Exceptions.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Vfps.Data.Models;

namespace Vfps.Data;

public class PseudonymContext(DbContextOptions<PseudonymContext> options) : DbContext(options)
{
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseExceptionProcessor();
        optionsBuilder.UseSnakeCaseNamingConvention();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Pseudonym>().HasKey(c => new { c.NamespaceName, c.OriginalValue });

        // Keyset/seek pagination for PseudonymAppService.ListAsync - lets `List` page through
        // hundreds of millions of rows per namespace without the cost of OFFSET, which grows
        // linearly with page depth.
        modelBuilder
            .Entity<Pseudonym>()
            .HasIndex(p => new
            {
                p.NamespaceName,
                p.CreatedAt,
                p.OriginalValue,
            })
            .HasDatabaseName("ix_pseudonyms_namespace_name_created_at_original_value")
            .IsCreatedConcurrently();

        // Reverse lookup (pseudonym_value -> original_value) has no supporting index today -
        // without this, PseudonymAppService's future reverse-lookup path (and the existing Get
        // RPC) would scan the whole namespace partition of the primary key index.
        modelBuilder
            .Entity<Pseudonym>()
            .HasIndex(p => new { p.NamespaceName, p.PseudonymValue })
            .HasDatabaseName("ix_pseudonyms_namespace_name_pseudonym_value")
            .IsCreatedConcurrently();

        var columnMappingsConverter = new ValueConverter<List<ColumnMapping>, string>(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v =>
                JsonSerializer.Deserialize<List<ColumnMapping>>(v, (JsonSerializerOptions?)null)
                ?? new List<ColumnMapping>()
        );
        var columnMappingsComparer = new ValueComparer<List<ColumnMapping>>(
            (a, b) =>
                JsonSerializer.Serialize(a, (JsonSerializerOptions?)null)
                == JsonSerializer.Serialize(b, (JsonSerializerOptions?)null),
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null).GetHashCode(),
            v =>
                JsonSerializer.Deserialize<List<ColumnMapping>>(
                    JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    (JsonSerializerOptions?)null
                )!
        );

        var columnMappingsProperty = modelBuilder
            .Entity<PseudonymizationJob>()
            .Property(j => j.ColumnMappings);
        columnMappingsProperty.HasConversion(columnMappingsConverter, columnMappingsComparer);
        if (Database.IsNpgsql())
        {
            columnMappingsProperty.HasColumnType("jsonb");
        }

        // via https://blog.dangl.me/archive/handling-datetimeoffset-in-sqlite-with-entity-framework-core/
        // only really relevant for unit/integration-testing
        if (Database.IsSqlite())
        {
            // SQLite does not have proper support for DateTimeOffset via Entity Framework Core, see the limitations
            // here: https://docs.microsoft.com/en-us/ef/core/providers/sqlite/limitations#query-limitations
            // To work around this, when the Sqlite database provider is used, all model properties of type DateTimeOffset
            // use the DateTimeOffsetToBinaryConverter
            // Based on: https://github.com/aspnet/EntityFrameworkCore/issues/10784#issuecomment-415769754
            // This only supports millisecond precision, but should be sufficient for most use cases.
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var properties = entityType
                    .ClrType.GetProperties()
                    .Where(p =>
                        p.PropertyType == typeof(DateTimeOffset)
                        || p.PropertyType == typeof(DateTimeOffset?)
                    );
                foreach (var property in properties)
                {
                    modelBuilder
                        .Entity(entityType.Name)
                        .Property(property.Name)
                        .HasConversion(new DateTimeOffsetToStringConverter());
                }
            }
        }
    }

    public DbSet<Pseudonym> Pseudonyms { get; set; }
    public DbSet<Namespace> Namespaces { get; set; }
    public DbSet<PseudonymizationJob> PseudonymizationJobs { get; set; }
}
