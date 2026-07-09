using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vfps.Migrations
{
    /// <inheritdoc />
    public partial class AddPseudonymKeysetAndReverseLookupIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder
                .CreateIndex(
                    name: "ix_pseudonyms_namespace_name_created_at_original_value",
                    table: "pseudonyms",
                    columns: new[] { "namespace_name", "created_at", "original_value" }
                )
                .Annotation("Npgsql:CreatedConcurrently", true);

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
                name: "ix_pseudonyms_namespace_name_created_at_original_value",
                table: "pseudonyms"
            );

            migrationBuilder.DropIndex(
                name: "ix_pseudonyms_namespace_name_pseudonym_value",
                table: "pseudonyms"
            );
        }
    }
}
