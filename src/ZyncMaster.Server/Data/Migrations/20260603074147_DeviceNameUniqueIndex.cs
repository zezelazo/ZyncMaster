using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZyncMaster.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class DeviceNameUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NameLower",
                table: "Devices",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            // Backfill the derived key from the existing user-typed name BEFORE the unique index is
            // created — otherwise every pre-existing row would share the "" default and a user with
            // two or more devices would break the new unique (UserId, NameLower) constraint. LTRIM/RTRIM
            // mirrors the trimming the store applies when writing NameLower for new/updated rows.
            migrationBuilder.Sql(
                "UPDATE [Devices] SET [NameLower] = LOWER(LTRIM(RTRIM([Name])));");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_UserId_NameLower",
                table: "Devices",
                columns: new[] { "UserId", "NameLower" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Devices_UserId_NameLower",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "NameLower",
                table: "Devices");
        }
    }
}
