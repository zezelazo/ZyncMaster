using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZyncMaster.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClipboardModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClipboardDeviceSettings",
                columns: table => new
                {
                    DeviceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AutoSync = table.Column<bool>(type: "bit", nullable: false),
                    Send = table.Column<bool>(type: "bit", nullable: false),
                    Receive = table.Column<bool>(type: "bit", nullable: false),
                    ViewerHotkey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Density = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ShowHints = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClipboardDeviceSettings", x => x.DeviceId);
                });

            migrationBuilder.CreateTable(
                name: "ClipboardItems",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    OriginDeviceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    OriginDeviceName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    Payload = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Thumbnail = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    Preview = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClipboardItems", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClipboardDeviceSettings_UserId",
                table: "ClipboardDeviceSettings",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ClipboardItems_UserId_CreatedUtc",
                table: "ClipboardItems",
                columns: new[] { "UserId", "CreatedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClipboardDeviceSettings");

            migrationBuilder.DropTable(
                name: "ClipboardItems");
        }
    }
}
