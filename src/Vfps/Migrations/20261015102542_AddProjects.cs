using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vfps.Migrations
{
    /// <inheritdoc />
    public partial class AddProjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
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
                    table.PrimaryKey("pk_projects", x => x.name);
                }
            );

            migrationBuilder.CreateTable(
                name: "project_namespaces",
                columns: table => new
                {
                    project_name = table.Column<string>(type: "text", nullable: false),
                    namespace_name = table.Column<string>(type: "text", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey(
                        "pk_project_namespaces",
                        x => new { x.project_name, x.namespace_name }
                    );
                    table.ForeignKey(
                        name: "fk_project_namespaces_namespaces_namespace_name",
                        column: x => x.namespace_name,
                        principalTable: "namespaces",
                        principalColumn: "name",
                        onDelete: ReferentialAction.Cascade
                    );
                    table.ForeignKey(
                        name: "fk_project_namespaces_projects_project_name",
                        column: x => x.project_name,
                        principalTable: "projects",
                        principalColumn: "name",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "ix_project_namespaces_namespace_name",
                table: "project_namespaces",
                column: "namespace_name"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "project_namespaces");

            migrationBuilder.DropTable(name: "projects");
        }
    }
}
