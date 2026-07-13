using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vfps.Migrations
{
    /// <inheritdoc />
    public partial class AddPseudonymNamespaceValueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder
                .CreateIndex(
                    name: "ix_pseudonyms_namespace_name_pseudonym_value",
                    table: "pseudonyms",
                    columns: new[] { "namespace_name", "pseudonym_value" }
                )
                .Annotation("Npgsql:CreatedConcurrently", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_pseudonyms_namespace_name_pseudonym_value",
                table: "pseudonyms"
            );
        }
    }
}
