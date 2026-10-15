using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vfps.Migrations
{
    /// <inheritdoc />
    public partial class AddPseudonymAppendRequestIdIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder
                .CreateIndex(
                    name: "ix_pseudonyms_namespace_name_append_request_id",
                    table: "pseudonyms",
                    columns: new[] { "namespace_name", "append_request_id" },
                    unique: true,
                    filter: "append_request_id IS NOT NULL"
                )
                .Annotation("Npgsql:CreatedConcurrently", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_pseudonyms_namespace_name_append_request_id",
                table: "pseudonyms"
            );
        }
    }
}
