using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZyncMaster.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class CalendarV2Replicas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PrefixRules",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Prefix = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MaskTitle = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrefixRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrefixRules_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReplicaLinks",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceAccountId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SourceEventId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SourceGraphEventId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    SourceKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DestinationAccountId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DestinationCalendarId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    DestinationEventId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    MaskTitle = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RuleId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReplicaLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReplicaLinks_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PrefixRuleDestinations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RuleId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AccountId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CalendarId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrefixRuleDestinations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrefixRuleDestinations_PrefixRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "PrefixRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PrefixRuleDestinations_RuleId",
                table: "PrefixRuleDestinations",
                column: "RuleId");

            migrationBuilder.CreateIndex(
                name: "IX_PrefixRules_UserId_SortOrder",
                table: "PrefixRules",
                columns: new[] { "UserId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_ReplicaLinks_UserId_SourceEventId",
                table: "ReplicaLinks",
                columns: new[] { "UserId", "SourceEventId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReplicaLinks_UserId_Status",
                table: "ReplicaLinks",
                columns: new[] { "UserId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PrefixRuleDestinations");

            migrationBuilder.DropTable(
                name: "ReplicaLinks");

            migrationBuilder.DropTable(
                name: "PrefixRules");
        }
    }
}
