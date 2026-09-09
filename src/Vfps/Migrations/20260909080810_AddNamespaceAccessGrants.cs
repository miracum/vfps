using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vfps.Migrations
{
    /// <inheritdoc />
    public partial class AddNamespaceAccessGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "namespace_access_grants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    namespace_name = table.Column<string>(type: "text", nullable: true),
                    grantee_type = table.Column<int>(type: "integer", nullable: false),
                    grantee = table.Column<string>(type: "text", nullable: false),
                    can_read = table.Column<bool>(type: "boolean", nullable: false),
                    can_write = table.Column<bool>(type: "boolean", nullable: false),
                    can_reverse_lookup = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    last_updated_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_namespace_access_grants", x => x.id);
                    table.ForeignKey(
                        name: "fk_namespace_access_grants_namespaces_namespace_name",
                        column: x => x.namespace_name,
                        principalTable: "namespaces",
                        principalColumn: "name",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "ix_namespace_access_grants_global_grantee",
                table: "namespace_access_grants",
                columns: new[] { "grantee_type", "grantee" },
                unique: true,
                filter: "namespace_name IS NULL"
            );

            migrationBuilder.CreateIndex(
                name: "ix_namespace_access_grants_namespace_grantee",
                table: "namespace_access_grants",
                columns: new[] { "namespace_name", "grantee_type", "grantee" },
                unique: true,
                filter: "namespace_name IS NOT NULL"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "namespace_access_grants");
        }
    }
}
