using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vfps.Migrations
{
    /// <inheritdoc />
    public partial class MakePseudonymizationJobInputObjectKeyNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "input_object_key",
                table: "pseudonymization_jobs",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "input_object_key",
                table: "pseudonymization_jobs",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true
            );
        }
    }
}
