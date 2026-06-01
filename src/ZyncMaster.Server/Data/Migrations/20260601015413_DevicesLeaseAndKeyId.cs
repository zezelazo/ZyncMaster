using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZyncMaster.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class DevicesLeaseAndKeyId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AppVersion",
                table: "Devices",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasOutlookCom",
                table: "Devices",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "KeyId",
                table: "Devices",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeaseUntil",
                table: "Devices",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Platform",
                table: "Devices",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "windows");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_KeyId",
                table: "Devices",
                column: "KeyId",
                filter: "[KeyId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Devices_KeyId",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "AppVersion",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "HasOutlookCom",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "KeyId",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "LeaseUntil",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "Platform",
                table: "Devices");
        }
    }
}
