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
        // Self-referencing parent/child hierarchy. DeleteBehavior.Restrict rather than the
        // Cascade used for a namespace's pseudonyms: deleting a namespace that still has
        // children would otherwise silently destroy every downstream pseudonymization level.
        // NamespaceAppService.DeleteAsync checks for children up front so callers get a clear
        // error; this constraint is what makes that guarantee hold under a race.
        modelBuilder
            .Entity<Namespace>()
            .HasOne(n => n.Parent)
            .WithMany(n => n.Children)
            .HasForeignKey(n => n.ParentName)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Namespace>().HasIndex(n => n.ParentName);

        // Cascade (unlike the parent/child relationship above): a namespace's access rules have
        // no meaning once the namespace is gone, and letting them linger would silently re-grant
        // access if a namespace were ever re-created under the same name.
        modelBuilder
            .Entity<NamespaceAccessGrant>()
            .HasOne(g => g.Namespace)
            .WithMany(n => n.AccessGrants)
            .HasForeignKey(g => g.NamespaceName)
            .OnDelete(DeleteBehavior.Cascade);

        // Two partial indexes rather than one over the whole triple: PostgreSQL treats NULLs as
        // distinct in a unique index, so a plain unique index would happily accept any number of
        // duplicate "all namespaces" (namespace_name IS NULL) grants for the same grantee. The
        // NULL and non-NULL cases therefore get their own index each. NamespaceAccessGrantAppService
        // rejects duplicates up front for a readable error; these are what hold the line under a
        // race between two admins.
        modelBuilder
            .Entity<NamespaceAccessGrant>()
            .HasIndex(g => new
            {
                g.NamespaceName,
                g.GranteeType,
                g.Grantee,
            })
            .HasDatabaseName("ix_namespace_access_grants_namespace_grantee")
            .HasFilter("namespace_name IS NOT NULL")
            .IsUnique();

        modelBuilder
            .Entity<NamespaceAccessGrant>()
            .HasIndex(g => new { g.GranteeType, g.Grantee })
            .HasDatabaseName("ix_namespace_access_grants_global_grantee")
            .HasFilter("namespace_name IS NULL")
            .IsUnique();

        // SequenceNumber is part of the key (rather than just (NamespaceName, OriginalValue)) so
        // a multi-psn namespace (Namespace.AllowsMultiplePseudonyms) can store more than one
        // pseudonym per original value. It's always 0 for a namespace that never allows more than
        // one, so this is a no-op for every other namespace's behavior.
        modelBuilder
            .Entity<Pseudonym>()
            .HasKey(c => new
            {
                c.NamespaceName,
                c.OriginalValue,
                c.SequenceNumber,
            });

        // Keyset/seek pagination for PseudonymAppService.ListAsync - lets `List` page through
        // hundreds of millions of rows per namespace without the cost of OFFSET, which grows
        // linearly with page depth. SequenceNumber is included as the final tie-breaker since
        // OriginalValue alone is no longer guaranteed unique per namespace under multi-psn.
        modelBuilder
            .Entity<Pseudonym>()
            .HasIndex(p => new
            {
                p.NamespaceName,
                p.CreatedAt,
                p.OriginalValue,
                p.SequenceNumber,
            })
            .HasDatabaseName(
                "ix_pseudonyms_namespace_name_created_at_original_value_sequence_number"
            )
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

        // Same JSON-column treatment as ColumnMappings above: a MetricSnapshot's values are only
        // ever read and written as a whole set, so they live in one column rather than a row per
        // series - which is also what makes "replace wholesale" (and therefore a series that has
        // gone away actually disappearing) a single write.
        var metricValuesConverter = new ValueConverter<Dictionary<string, long>, string>(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v =>
                JsonSerializer.Deserialize<Dictionary<string, long>>(
                    v,
                    (JsonSerializerOptions?)null
                ) ?? new Dictionary<string, long>()
        );
        var metricValuesComparer = new ValueComparer<Dictionary<string, long>>(
            (a, b) =>
                JsonSerializer.Serialize(a, (JsonSerializerOptions?)null)
                == JsonSerializer.Serialize(b, (JsonSerializerOptions?)null),
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null).GetHashCode(),
            v =>
                JsonSerializer.Deserialize<Dictionary<string, long>>(
                    JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    (JsonSerializerOptions?)null
                )!
        );

        modelBuilder.Entity<MetricSnapshot>().HasKey(s => s.Name);

        var metricValuesProperty = modelBuilder.Entity<MetricSnapshot>().Property(s => s.Values);
        metricValuesProperty.HasConversion(metricValuesConverter, metricValuesComparer);
        if (Database.IsNpgsql())
        {
            metricValuesProperty.HasColumnType("jsonb");
        }

        // Seeded rather than created on demand: an upsert-if-missing would need its own race
        // handling on first startup across replicas, whereas HasData puts the row there for both a
        // migrated database and a test's EnsureCreated. DateTimeOffset.MinValue is simply "never
        // computed", so the first replica to look wins the claim immediately.
        modelBuilder
            .Entity<MetricSnapshot>()
            .HasData(
                new MetricSnapshot
                {
                    Name = MetricSnapshot.PseudonymCountsName,
                    Values = [],
                    ComputedAt = DateTimeOffset.MinValue,
                }
            );

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
    public DbSet<MetricSnapshot> MetricSnapshots { get; set; }
    public DbSet<NamespaceAccessGrant> NamespaceAccessGrants { get; set; }
}
