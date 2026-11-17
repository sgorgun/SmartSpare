using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSpare.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class UpdateSecurityStampComment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "security_stamp",
                schema: "iam",
                table: "app_user",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()",
                comment: "Changes on password/role change; invalidates issued sign-in cookies (security stamp validation).",
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldDefaultValueSql: "gen_random_uuid()",
                oldComment: "Changes on password/role change; invalidates issued JWT refresh tokens.");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "security_stamp",
                schema: "iam",
                table: "app_user",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()",
                comment: "Changes on password/role change; invalidates issued JWT refresh tokens.",
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldDefaultValueSql: "gen_random_uuid()",
                oldComment: "Changes on password/role change; invalidates issued sign-in cookies (security stamp validation).");
        }
    }
}
