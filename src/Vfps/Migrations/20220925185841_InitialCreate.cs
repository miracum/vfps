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
                name: "Namespaces",
                columns: table => new
                {
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    PseudonymGenerationMethod = table.Column<int>(type: "integer", nullable: false),
                    PseudonymLength = table.Column<long>(type: "bigint", nullable: false),
                    PseudonymPrefix = table.Column<string>(type: "text", nullable: true),
                    PseudonymSuffix = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastUpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Namespaces", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "Pseudonyms",
                columns: table => new
                {
                    OriginalValue = table.Column<string>(type: "text", nullable: false),
                    NamespaceName = table.Column<string>(type: "text", nullable: false),
                    PseudonymValue = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastUpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Pseudonyms", x => new { x.NamespaceName, x.OriginalValue });
                    table.ForeignKey(
                        name: "FK_Pseudonyms_Namespaces_NamespaceName",
                        column: x => x.NamespaceName,
                        principalTable: "Namespaces",
                        principalColumn: "Name",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Pseudonyms");

            migrationBuilder.DropTable(
                name: "Namespaces");
        }
    }
}
