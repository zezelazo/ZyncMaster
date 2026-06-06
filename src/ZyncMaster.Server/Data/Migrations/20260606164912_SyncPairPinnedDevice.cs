using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZyncMaster.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class SyncPairPinnedDevice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PinnedDeviceId",
                table: "SyncPairs",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SyncRequestedUtc",
                table: "SyncPairs",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncPairs_PinnedDeviceId",
                table: "SyncPairs",
                column: "PinnedDeviceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SyncPairs_PinnedDeviceId",
                table: "SyncPairs");

            migrationBuilder.DropColumn(
                name: "PinnedDeviceId",
                table: "SyncPairs");

            migrationBuilder.DropColumn(
                name: "SyncRequestedUtc",
                table: "SyncPairs");
        }
    }
}
