using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vfps.Migrations
{
    /// <inheritdoc />
    public partial class CreatePseudonymizationJobsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pseudonymization_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    input_object_key = table.Column<string>(type: "text", nullable: false),
                    output_object_key = table.Column<string>(type: "text", nullable: true),
                    encoding = table.Column<string>(type: "text", nullable: false),
                    delimiter = table.Column<string>(type: "text", nullable: false),
                    has_header_row = table.Column<bool>(type: "boolean", nullable: false),
                    column_mappings = table.Column<string>(type: "jsonb", nullable: false),
                    total_bytes = table.Column<long>(type: "bigint", nullable: false),
                    bytes_processed = table.Column<long>(type: "bigint", nullable: false),
                    rows_processed = table.Column<long>(type: "bigint", nullable: false),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    hangfire_job_id = table.Column<string>(type: "text", nullable: true),
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
                    table.PrimaryKey("pk_pseudonymization_jobs", x => x.id);
                }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "pseudonymization_jobs");
        }
    }
}
