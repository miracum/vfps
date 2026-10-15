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

        // Project membership is a plain many-to-many join with a composite key. Both sides cascade,
        // which is safe in a way the parent/child relationship's cascade would not be: it only
        // ever removes the membership rows, never a namespace (and so never a pseudonym) or a
        // project. The namespace side has no navigation back - which projects a namespace is in is
        // answered through IProjectRepository, so the namespace entity (and with it the namespace
        // cache) stays unaware of projects.
        modelBuilder
            .Entity<ProjectNamespace>()
            .HasKey(m => new { m.ProjectName, m.NamespaceName });
        modelBuilder
            .Entity<ProjectNamespace>()
            .HasOne<Project>()
            .WithMany(p => p.Memberships)
            .HasForeignKey(m => m.ProjectName)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder
            .Entity<ProjectNamespace>()
            .HasOne<Namespace>()
            .WithMany()
            .HasForeignKey(m => m.NamespaceName)
            .OnDelete(DeleteBehavior.Cascade);

        // The key already serves "which namespaces are in this project"; this serves the reverse.
        modelBuilder.Entity<ProjectNamespace>().HasIndex(m => m.NamespaceName);

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

        // Backs Append's request_id (AIP-155): a retried call finds the pseudonym its first attempt
        // stored, and two concurrent attempts with the same id can't both store one. Partial, as
        // the column is NULL for every pseudonym not stored by an Append that carried an id - the
        // overwhelming majority - which keeps the index tiny on a table that isn't.
        modelBuilder
            .Entity<Pseudonym>()
            .HasIndex(p => new { p.NamespaceName, p.AppendRequestId })
            .HasDatabaseName("ix_pseudonyms_namespace_name_append_request_id")
            .HasFilter("append_request_id IS NOT NULL")
            .IsUnique()
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

        // One ordinary row per namespace, so there is nothing to configure beyond the key and the
        // foreign key: no value converter, no comparer, and no seeded row (the recompute inserts
        // what it finds). Cascade delete is what makes a removed namespace's count disappear with
        // it, rather than lingering as an exported series until the next recompute sweeps it.
        modelBuilder.Entity<PseudonymCount>().HasKey(c => c.NamespaceName);
        modelBuilder
            .Entity<PseudonymCount>()
            .HasOne<Namespace>()
            .WithMany()
            .HasForeignKey(c => c.NamespaceName)
            .OnDelete(DeleteBehavior.Cascade);

        // Access tokens and the service accounts they can belong to.
        //
        // One table for both token types (see AccessToken): the credential, its expiry and its
        // revocation are identical either way, and a single table is what lets one authentication
        // handler and one cache serve both.
        modelBuilder.Entity<AccessToken>().HasKey(t => t.Id);

        // The public half of the token string. Unique because it is what an incoming token is
        // resolved by - a duplicate would make authentication ambiguous rather than merely
        // untidy.
        modelBuilder
            .Entity<AccessToken>()
            .HasIndex(t => t.TokenId)
            .HasDatabaseName("ix_access_tokens_token_id")
            .IsUnique();

        // "My tokens" is the only listing a non-admin ever performs, and it runs on every visit
        // to the access-tokens page.
        modelBuilder
            .Entity<AccessToken>()
            .HasIndex(t => t.Subject)
            .HasDatabaseName("ix_access_tokens_subject")
            .HasFilter("subject IS NOT NULL");

        // Cascade: a token that authenticates as an account that no longer exists could never
        // resolve to a principal again, so keeping it would only leave a live-looking credential
        // in the UI. The account's *grants* are deleted by ServiceAccountAppService, which has to
        // do it explicitly - Grantee is a plain string with no foreign key to cascade from.
        modelBuilder
            .Entity<AccessToken>()
            .HasOne(t => t.ServiceAccount)
            .WithMany(a => a.Tokens)
            .HasForeignKey(t => t.ServiceAccountName)
            .OnDelete(DeleteBehavior.Cascade);

        // Stored as JSON rather than a Postgres text[], for the same reason PseudonymizationJob's
        // column mappings are: the test suite runs this model on SQLite, which has no array type.
        var rolesConverter = new ValueConverter<List<string>, string>(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v =>
                JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null)
                ?? new List<string>()
        );
        var rolesComparer = new ValueComparer<List<string>>(
            (a, b) => a != null && b != null && a.SequenceEqual(b),
            v => v.Aggregate(0, (hash, role) => HashCode.Combine(hash, role.GetHashCode())),
            v => v.ToList()
        );
        var rolesProperty = modelBuilder.Entity<AccessToken>().Property(t => t.Roles);
        rolesProperty.HasConversion(rolesConverter, rolesComparer);
        if (Database.IsNpgsql())
        {
            rolesProperty.HasColumnType("jsonb");
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
    public DbSet<PseudonymCount> PseudonymCounts { get; set; }
    public DbSet<NamespaceAccessGrant> NamespaceAccessGrants { get; set; }
    public DbSet<Project> Projects { get; set; }
    public DbSet<ProjectNamespace> ProjectNamespaces { get; set; }
    public DbSet<ServiceAccount> ServiceAccounts { get; set; }
    public DbSet<AccessToken> AccessTokens { get; set; }
}
