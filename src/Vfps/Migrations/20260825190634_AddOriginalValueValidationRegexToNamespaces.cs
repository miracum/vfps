using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vfps.Migrations
{
    /// <inheritdoc />
    public partial class AddOriginalValueValidationRegexToNamespaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "original_value_validation_regex",
                table: "namespaces",
                type: "text",
                nullable: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "original_value_validation_regex",
                table: "namespaces"
            );
        }
    }
}
