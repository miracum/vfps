using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vfps.Migrations
{
    /// <inheritdoc />
    public partial class AddMetricSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "metric_snapshots",
                columns: table => new
                {
                    name = table.Column<string>(type: "text", nullable: false),
                    values = table.Column<string>(type: "jsonb", nullable: false),
                    computed_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "metric_snapshots");
        }
    }
}
