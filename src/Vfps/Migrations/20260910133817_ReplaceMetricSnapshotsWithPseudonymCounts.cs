using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vfps.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceMetricSnapshotsWithPseudonymCounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "metric_snapshots");

            migrationBuilder.CreateTable(
                name: "pseudonym_counts",
                columns: table => new
                {
                    namespace_name = table.Column<string>(type: "text", nullable: false),
                    count = table.Column<long>(type: "bigint", nullable: false),
                    computed_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pseudonym_counts", x => x.namespace_name);
                    table.ForeignKey(
                        name: "fk_pseudonym_counts_namespaces_namespace_name",
                        column: x => x.namespace_name,
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
            migrationBuilder.DropTable(name: "pseudonym_counts");

            migrationBuilder.CreateTable(
                name: "metric_snapshots",
                columns: table => new
                {
                    name = table.Column<string>(type: "text", nullable: false),
                    computed_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    values = table.Column<string>(type: "jsonb", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_metric_snapshots", x => x.name);
                }
            );

            migrationBuilder.InsertData(
                table: "metric_snapshots",
                columns: new[] { "name", "computed_at", "values" },
                values: new object[]
                {
                    "pseudonym-counts",
                    new DateTimeOffset(
                        new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified),
                        new TimeSpan(0, 0, 0, 0, 0)
                    ),
                    "{}",
                }
            );
        }
    }
}
