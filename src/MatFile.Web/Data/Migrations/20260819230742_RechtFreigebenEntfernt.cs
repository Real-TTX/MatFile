using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatFile.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class RechtFreigebenEntfernt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CanShare",
                table: "RootAccess");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CanShare",
                table: "RootAccess",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }
    }
}
