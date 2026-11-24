using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSpare.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// #69: user names are stored in canonical lower case (the citext regex alone lets upper case through), and the
    /// password_hash comment names the 220,000 PBKDF2 iterations of PR 3a.
    /// </summary>
    internal partial class LowerCaseUserNamesAndHashComment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_app_user_user_name",
                schema: "iam",
                table: "app_user");

            migrationBuilder.AlterColumn<string>(
                name: "password_hash",
                schema: "iam",
                table: "app_user",
                type: "text",
                nullable: true,
                comment: "ASP.NET Core Identity V3 hash (PBKDF2-HMAC-SHA512, 220,000 iterations; older hashes are rehashed at sign-in). Plain text is impossible by CHECK.",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true,
                oldComment: "ASP.NET Core Identity V3 hash (PBKDF2-HMAC-SHA512, 100k iterations). Plain text is impossible by CHECK.");

            migrationBuilder.AddCheckConstraint(
                name: "ck_app_user_user_name",
                schema: "iam",
                table: "app_user",
                sql: "((user_name ~ '^[a-z0-9._-]{3,50}$'::citext) AND ((user_name)::text = lower((user_name)::text)))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_app_user_user_name",
                schema: "iam",
                table: "app_user");

            migrationBuilder.AlterColumn<string>(
                name: "password_hash",
                schema: "iam",
                table: "app_user",
                type: "text",
                nullable: true,
                comment: "ASP.NET Core Identity V3 hash (PBKDF2-HMAC-SHA512, 100k iterations). Plain text is impossible by CHECK.",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true,
                oldComment: "ASP.NET Core Identity V3 hash (PBKDF2-HMAC-SHA512, 220,000 iterations; older hashes are rehashed at sign-in). Plain text is impossible by CHECK.");

            migrationBuilder.AddCheckConstraint(
                name: "ck_app_user_user_name",
                schema: "iam",
                table: "app_user",
                sql: "(user_name ~ '^[a-z0-9._-]{3,50}$'::citext)");
        }
    }
}
