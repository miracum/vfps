using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vfps.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "namespaces",
                columns: table => new
                {
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    pseudonymgenerationmethod = table.Column<int>(
                        name: "pseudonym_generation_method",
                        type: "integer",
                        nullable: false
                    ),
                    pseudonymlength = table.Column<long>(
                        name: "pseudonym_length",
                        type: "bigint",
                        nullable: false
                    ),
                    pseudonymprefix = table.Column<string>(
                        name: "pseudonym_prefix",
                        type: "text",
                        nullable: true
                    ),
                    pseudonymsuffix = table.Column<string>(
                        name: "pseudonym_suffix",
                        type: "text",
                        nullable: true
                    ),
                    createdat = table.Column<DateTimeOffset>(
                        name: "created_at",
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    lastupdatedat = table.Column<DateTimeOffset>(
                        name: "last_updated_at",
                        type: "timestamp with time zone",
                        nullable: false
                    )
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_namespaces", x => x.name);
                }
            );

            migrationBuilder.CreateTable(
                name: "pseudonyms",
                columns: table => new
                {
                    originalvalue = table.Column<string>(
                        name: "original_value",
                        type: "text",
                        nullable: false
                    ),
                    namespacename = table.Column<string>(
                        name: "namespace_name",
                        type: "text",
                        nullable: false
                    ),
                    pseudonymvalue = table.Column<string>(
                        name: "pseudonym_value",
                        type: "text",
                        nullable: false
                    ),
                    createdat = table.Column<DateTimeOffset>(
                        name: "created_at",
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    lastupdatedat = table.Column<DateTimeOffset>(
                        name: "last_updated_at",
                        type: "timestamp with time zone",
                        nullable: false
                    )
                },
                constraints: table =>
                {
                    table.PrimaryKey(
                        "pk_pseudonyms",
                        x => new { x.namespacename, x.originalvalue }
                    );
                    table.ForeignKey(
                        name: "fk_pseudonyms_namespaces_namespace_name",
                        column: x => x.namespacename,
                        principalTable: "namespaces",
                        principalColumn: "name",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "pseudonyms");

            migrationBuilder.DropTable(name: "namespaces");
        }
    }
}
