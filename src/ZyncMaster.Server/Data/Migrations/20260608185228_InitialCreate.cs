using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ZyncMaster.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClipboardDeviceSettings",
                columns: table => new
                {
                    DeviceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AutoSync = table.Column<bool>(type: "boolean", nullable: false),
                    Send = table.Column<bool>(type: "boolean", nullable: false),
                    Receive = table.Column<bool>(type: "boolean", nullable: false),
                    ViewerHotkey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Density = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ShowHints = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClipboardDeviceSettings", x => x.DeviceId);
                });

            migrationBuilder.CreateTable(
                name: "ClipboardItems",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OriginDeviceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OriginDeviceName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    Payload = table.Column<byte[]>(type: "bytea", nullable: false),
                    Thumbnail = table.Column<byte[]>(type: "bytea", nullable: true),
                    Preview = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClipboardItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConnectedAccounts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AccountRef = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EncryptedRefreshToken = table.Column<string>(type: "text", nullable: false),
                    ConnectedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectedAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DataProtectionKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FriendlyName = table.Column<string>(type: "text", nullable: true),
                    Xml = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataProtectionKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Devices",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    NameLower = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ApiKeyHash = table.Column<string>(type: "text", nullable: false),
                    TargetCalendarId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    KeyId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Platform = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "windows"),
                    HasOutlookCom = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    AppVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MagicLinks",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    Nonce = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MagicLinks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PendingPairings",
                columns: table => new
                {
                    PairingId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DeviceName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Approved = table.Column<bool>(type: "boolean", nullable: false),
                    ApprovedDeviceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    OneTimeApiKey = table.Column<string>(type: "text", nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    VerifierHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingPairings", x => x.PairingId);
                });

            migrationBuilder.CreateTable(
                name: "SyncPairs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SourceJson = table.Column<string>(type: "text", nullable: false),
                    DestinationJson = table.Column<string>(type: "text", nullable: false),
                    IntervalMin = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LastRunUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastResultJson = table.Column<string>(type: "text", nullable: true),
                    PendingCleanupJson = table.Column<string>(type: "text", nullable: true),
                    PinnedDeviceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SyncRequestedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncPairs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncRunLocks",
                columns: table => new
                {
                    PairId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LockedUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Owner = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    FenceToken = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRunLocks", x => x.PairId);
                });

            migrationBuilder.CreateTable(
                name: "SyncStates",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DeviceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LastSyncUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastCreated = table.Column<int>(type: "integer", nullable: false),
                    LastUpdated = table.Column<int>(type: "integer", nullable: false),
                    LastDeleted = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PrimaryEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Plan = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CalendarAccounts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AccountEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Authority = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Scope = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DeviceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EncryptedRefreshToken = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ConnectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CalendarAccounts_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IdentityAccessTokens",
                columns: table => new
                {
                    Jti = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityAccessTokens", x => x.Jti);
                    table.ForeignKey(
                        name: "FK_IdentityAccessTokens_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IdentityLogins",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProviderSubject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    EmailVerified = table.Column<bool>(type: "boolean", nullable: false),
                    LinkedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityLogins", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IdentityLogins_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IdentityRefreshTokens",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityRefreshTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IdentityRefreshTokens_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserToggles",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CloudFallbackSync = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserToggles", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_UserToggles_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "Users",
                columns: new[] { "Id", "CreatedUtc", "DisplayName", "Email", "Plan", "PrimaryEmail", "Provider", "Subject" },
                values: new object[] { "default", new DateTimeOffset(new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Default", null, null, "", "local", "default" });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarAccounts_UserId",
                table: "CalendarAccounts",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ClipboardDeviceSettings_UserId",
                table: "ClipboardDeviceSettings",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ClipboardItems_UserId_CreatedUtc",
                table: "ClipboardItems",
                columns: new[] { "UserId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedAccounts_UserId_AccountRef",
                table: "ConnectedAccounts",
                columns: new[] { "UserId", "AccountRef" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Devices_KeyId",
                table: "Devices",
                column: "KeyId",
                filter: "\"KeyId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_UserId",
                table: "Devices",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_UserId_NameLower",
                table: "Devices",
                columns: new[] { "UserId", "NameLower" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdentityAccessTokens_ExpiresAt",
                table: "IdentityAccessTokens",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityAccessTokens_UserId",
                table: "IdentityAccessTokens",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityLogins_Email_EmailVerified",
                table: "IdentityLogins",
                columns: new[] { "Email", "EmailVerified" });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityLogins_Provider_ProviderSubject",
                table: "IdentityLogins",
                columns: new[] { "Provider", "ProviderSubject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdentityLogins_UserId",
                table: "IdentityLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityRefreshTokens_TokenHash",
                table: "IdentityRefreshTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdentityRefreshTokens_UserId",
                table: "IdentityRefreshTokens",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_MagicLinks_Email",
                table: "MagicLinks",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_MagicLinks_TokenHash",
                table: "MagicLinks",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PendingPairings_Code",
                table: "PendingPairings",
                column: "Code");

            migrationBuilder.CreateIndex(
                name: "IX_SyncPairs_PinnedDeviceId",
                table: "SyncPairs",
                column: "PinnedDeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncPairs_UserId",
                table: "SyncPairs",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncStates_UserId_DeviceId",
                table: "SyncStates",
                columns: new[] { "UserId", "DeviceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Provider_Subject",
                table: "Users",
                columns: new[] { "Provider", "Subject" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CalendarAccounts");

            migrationBuilder.DropTable(
                name: "ClipboardDeviceSettings");

            migrationBuilder.DropTable(
                name: "ClipboardItems");

            migrationBuilder.DropTable(
                name: "ConnectedAccounts");

            migrationBuilder.DropTable(
                name: "DataProtectionKeys");

            migrationBuilder.DropTable(
                name: "Devices");

            migrationBuilder.DropTable(
                name: "IdentityAccessTokens");

            migrationBuilder.DropTable(
                name: "IdentityLogins");

            migrationBuilder.DropTable(
                name: "IdentityRefreshTokens");

            migrationBuilder.DropTable(
                name: "MagicLinks");

            migrationBuilder.DropTable(
                name: "PendingPairings");

            migrationBuilder.DropTable(
                name: "SyncPairs");

            migrationBuilder.DropTable(
                name: "SyncRunLocks");

            migrationBuilder.DropTable(
                name: "SyncStates");

            migrationBuilder.DropTable(
                name: "UserToggles");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
