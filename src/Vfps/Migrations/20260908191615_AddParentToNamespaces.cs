using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vfps.Migrations
{
    /// <inheritdoc />
    public partial class AddParentToNamespaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "parent_name",
                table: "namespaces",
                type: "text",
                nullable: true
            );

            migrationBuilder.AddColumn<int>(
                name: "parent_validation_mode",
                table: "namespaces",
                type: "integer",
                nullable: false,
                defaultValue: 0
            );

            migrationBuilder.CreateIndex(
                name: "ix_namespaces_parent_name",
                table: "namespaces",
                column: "parent_name"
            );

            migrationBuilder.AddForeignKey(
                name: "fk_namespaces_namespaces_parent_name",
                table: "namespaces",
                column: "parent_name",
                principalTable: "namespaces",
                principalColumn: "name",
                onDelete: ReferentialAction.Restrict
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_namespaces_namespaces_parent_name",
                table: "namespaces"
            );

            migrationBuilder.DropIndex(name: "ix_namespaces_parent_name", table: "namespaces");

            migrationBuilder.DropColumn(name: "parent_name", table: "namespaces");

            migrationBuilder.DropColumn(name: "parent_validation_mode", table: "namespaces");
        }
    }
}
