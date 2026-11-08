using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bindery.Host.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCirculationAndNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RequesterSubject",
                table: "Jobs",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CirculationAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Owner = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Plugin = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    LibraryName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    FieldsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectedSecrets = table.Column<string>(type: "TEXT", nullable: false),
                    PickupLocationsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSyncedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastAttemptAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    NeedsAttention = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CirculationAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationPreferences",
                columns: table => new
                {
                    Subject = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    EmailEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    MutedCategories = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationPreferences", x => x.Subject);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Recipient = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    SentAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CirculationItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CatalogId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    State = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Authors = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Medium = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Format = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    CoverUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    QueuePosition = table.Column<int>(type: "INTEGER", nullable: true),
                    QueueSize = table.Column<int>(type: "INTEGER", nullable: true),
                    PickupLocation = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ReadyUntil = table.Column<long>(type: "INTEGER", nullable: true),
                    SuspendedUntil = table.Column<long>(type: "INTEGER", nullable: true),
                    DueAt = table.Column<long>(type: "INTEGER", nullable: true),
                    PlacedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    RemindedFor = table.Column<long>(type: "INTEGER", nullable: true),
                    FirstSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    StateChangedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CirculationItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CirculationItems_CirculationAccounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "CirculationAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CirculationEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    FromState = table.Column<string>(type: "TEXT", maxLength: 24, nullable: true),
                    ToState = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CirculationEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CirculationEvents_CirculationItems_ItemId",
                        column: x => x.ItemId,
                        principalTable: "CirculationItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CirculationAccounts_LastAttemptAt",
                table: "CirculationAccounts",
                column: "LastAttemptAt");

            migrationBuilder.CreateIndex(
                name: "IX_CirculationAccounts_Owner",
                table: "CirculationAccounts",
                column: "Owner");

            migrationBuilder.CreateIndex(
                name: "IX_CirculationEvents_ItemId_At",
                table: "CirculationEvents",
                columns: new[] { "ItemId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_CirculationItems_AccountId_Kind_ExternalId",
                table: "CirculationItems",
                columns: new[] { "AccountId", "Kind", "ExternalId" });

            migrationBuilder.CreateIndex(
                name: "IX_CirculationItems_State",
                table: "CirculationItems",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_Recipient_CreatedAt",
                table: "Notifications",
                columns: new[] { "Recipient", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_Status_NextAttemptAt",
                table: "Notifications",
                columns: new[] { "Status", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CirculationEvents");

            migrationBuilder.DropTable(
                name: "NotificationPreferences");

            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "CirculationItems");

            migrationBuilder.DropTable(
                name: "CirculationAccounts");

            migrationBuilder.DropColumn(
                name: "RequesterSubject",
                table: "Jobs");
        }
    }
}
