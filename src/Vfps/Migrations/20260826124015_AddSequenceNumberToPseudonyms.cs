using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vfps.Migrations
{
    /// <inheritdoc />
    public partial class AddSequenceNumberToPseudonyms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(name: "pk_pseudonyms", table: "pseudonyms");

            migrationBuilder.DropIndex(
                name: "ix_pseudonyms_namespace_name_created_at_original_value",
                table: "pseudonyms"
            );

            migrationBuilder.AddColumn<long>(
                name: "sequence_number",
                table: "pseudonyms",
                type: "bigint",
                nullable: false,
                defaultValue: 0L
            );

            migrationBuilder.AddColumn<bool>(
                name: "allows_multiple_pseudonyms",
                table: "namespaces",
                type: "boolean",
                nullable: false,
                defaultValue: false
            );

            migrationBuilder.AddPrimaryKey(
                name: "pk_pseudonyms",
                table: "pseudonyms",
                columns: new[] { "namespace_name", "original_value", "sequence_number" }
            );

            migrationBuilder
                .CreateIndex(
                    name: "ix_pseudonyms_namespace_name_created_at_original_value_sequence_number",
                    table: "pseudonyms",
                    columns: new[]
                    {
                        "namespace_name",
                        "created_at",
                        "original_value",
                        "sequence_number",
                    }
                )
                .Annotation("Npgsql:CreatedConcurrently", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(name: "pk_pseudonyms", table: "pseudonyms");

            migrationBuilder.DropIndex(
                name: "ix_pseudonyms_namespace_name_created_at_original_value_sequence_number",
                table: "pseudonyms"
            );

            migrationBuilder.DropColumn(name: "sequence_number", table: "pseudonyms");

            migrationBuilder.DropColumn(name: "allows_multiple_pseudonyms", table: "namespaces");

            migrationBuilder.AddPrimaryKey(
                name: "pk_pseudonyms",
                table: "pseudonyms",
                columns: new[] { "namespace_name", "original_value" }
            );

            migrationBuilder
                .CreateIndex(
                    name: "ix_pseudonyms_namespace_name_created_at_original_value",
                    table: "pseudonyms",
                    columns: new[] { "namespace_name", "created_at", "original_value" }
                )
                .Annotation("Npgsql:CreatedConcurrently", true);
        }
    }
}
