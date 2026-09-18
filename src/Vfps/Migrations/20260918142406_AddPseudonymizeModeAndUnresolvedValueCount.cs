using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vfps.Migrations
{
    /// <inheritdoc />
    public partial class AddPseudonymizeModeAndUnresolvedValueCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "pseudonymize_mode",
                table: "pseudonymization_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0
            );

            migrationBuilder.AddColumn<int>(
                name: "unresolved_value_count",
                table: "pseudonymization_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "pseudonymize_mode", table: "pseudonymization_jobs");

            migrationBuilder.DropColumn(
                name: "unresolved_value_count",
                table: "pseudonymization_jobs"
            );
        }
    }
}
