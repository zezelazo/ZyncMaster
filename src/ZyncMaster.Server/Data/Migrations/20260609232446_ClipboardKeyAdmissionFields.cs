using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZyncMaster.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClipboardKeyAdmissionFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NeedsTextKey",
                table: "ClipboardDeviceSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PublicKeyBase64",
                table: "ClipboardDeviceSettings",
                type: "character varying(4096)",
                maxLength: 4096,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NeedsTextKey",
                table: "ClipboardDeviceSettings");

            migrationBuilder.DropColumn(
                name: "PublicKeyBase64",
                table: "ClipboardDeviceSettings");
        }
    }
}
