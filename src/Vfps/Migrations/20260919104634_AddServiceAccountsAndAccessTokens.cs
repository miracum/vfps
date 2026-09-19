using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vfps.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceAccountsAndAccessTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "service_accounts",
                columns: table => new
                {
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    created_by = table.Column<string>(type: "text", nullable: false),
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
                    table.PrimaryKey("pk_service_accounts", x => x.name);
                }
            );

            migrationBuilder.CreateTable(
                name: "access_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_type = table.Column<int>(type: "integer", nullable: false),
                    token_id = table.Column<string>(type: "text", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    revoked_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    last_used_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: true),
                    username = table.Column<string>(type: "text", nullable: true),
                    email = table.Column<string>(type: "text", nullable: true),
                    roles = table.Column<string>(type: "jsonb", nullable: false),
                    service_account_name = table.Column<string>(type: "text", nullable: true),
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
                    table.PrimaryKey("pk_access_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_access_tokens_service_accounts_service_account_name",
                        column: x => x.service_account_name,
                        principalTable: "service_accounts",
                        principalColumn: "name",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "ix_access_tokens_service_account_name",
                table: "access_tokens",
                column: "service_account_name"
            );

            migrationBuilder.CreateIndex(
                name: "ix_access_tokens_subject",
                table: "access_tokens",
                column: "subject",
                filter: "subject IS NOT NULL"
            );

            migrationBuilder.CreateIndex(
                name: "ix_access_tokens_token_id",
                table: "access_tokens",
                column: "token_id",
                unique: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "access_tokens");

            migrationBuilder.DropTable(name: "service_accounts");
        }
    }
}
