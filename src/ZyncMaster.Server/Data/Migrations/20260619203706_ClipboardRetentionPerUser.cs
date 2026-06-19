using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZyncMaster.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClipboardRetentionPerUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ClipboardRetentionHours",
                table: "Users",
                type: "integer",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "Users",
                keyColumn: "Id",
                keyValue: "default",
                column: "ClipboardRetentionHours",
                value: null);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClipboardRetentionHours",
                table: "Users");
        }
    }
}
