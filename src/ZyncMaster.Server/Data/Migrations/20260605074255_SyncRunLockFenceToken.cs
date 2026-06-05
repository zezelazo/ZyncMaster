using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZyncMaster.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class SyncRunLockFenceToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FenceToken",
                table: "SyncRunLocks",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FenceToken",
                table: "SyncRunLocks");
        }
    }
}
