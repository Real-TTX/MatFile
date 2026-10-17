using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatFile.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class MatIdentityAnmeldung : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdToken",
                table: "UserSession",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalSubject",
                table: "User",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_User_ExternalSubject",
                table: "User",
                column: "ExternalSubject",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_User_ExternalSubject",
                table: "User");

            migrationBuilder.DropColumn(
                name: "IdToken",
                table: "UserSession");

            migrationBuilder.DropColumn(
                name: "ExternalSubject",
                table: "User");
        }
    }
}
