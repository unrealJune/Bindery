using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bindery.Host.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUsersAndShelves : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PluginSettings_Plugin_Key",
                table: "PluginSettings");

            migrationBuilder.AddColumn<string>(
                name: "Owner",
                table: "PluginSettings",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Shelves",
                columns: table => new
                {
                    BookId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Owner = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    AddedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Shelves", x => new { x.BookId, x.Owner });
                    table.ForeignKey(
                        name: "FK_Shelves_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Subject = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    FirstSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Subject);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PluginSettings_Plugin_Key",
                table: "PluginSettings",
                columns: new[] { "Plugin", "Key" },
                unique: true,
                filter: "\"Owner\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PluginSettings_Plugin_Owner_Key",
                table: "PluginSettings",
                columns: new[] { "Plugin", "Owner", "Key" },
                unique: true,
                filter: "\"Owner\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Shelves_Owner",
                table: "Shelves",
                column: "Owner");

            migrationBuilder.CreateIndex(
                name: "IX_Users_FirstSeenAt",
                table: "Users",
                column: "FirstSeenAt");

            // Everyone the database already knows of, first seen at their earliest trace.
            // The earliest of them is who the existing library is shelved for at startup
            // (UserDirectory.AdoptOrphansAsync) — done there rather than here so the
            // sidecars on disk are written too, and a rescan agrees.
            migrationBuilder.Sql("""
                INSERT INTO "Users" ("Subject", "DisplayName", "Email", "FirstSeenAt", "LastSeenAt")
                SELECT seen."Subject",
                       NULL,
                       (SELECT p."Email" FROM "NotificationPreferences" p WHERE p."Subject" = seen."Subject"),
                       MIN(seen."At"),
                       MAX(seen."At")
                FROM (
                    SELECT "Subject", "CreatedAt" AS "At" FROM "FeedTokens"
                    UNION ALL
                    SELECT "RequesterSubject", "CreatedAt" FROM "Jobs" WHERE "RequesterSubject" IS NOT NULL
                    UNION ALL
                    SELECT "Owner", "CreatedAt" FROM "CirculationAccounts"
                    UNION ALL
                    SELECT "Subject", "UpdatedAt" FROM "NotificationPreferences"
                ) AS seen
                WHERE seen."Subject" <> '' AND seen."Subject" <> 'unknown'
                GROUP BY seen."Subject";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Shelves");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropIndex(
                name: "IX_PluginSettings_Plugin_Key",
                table: "PluginSettings");

            migrationBuilder.DropIndex(
                name: "IX_PluginSettings_Plugin_Owner_Key",
                table: "PluginSettings");

            migrationBuilder.DropColumn(
                name: "Owner",
                table: "PluginSettings");

            migrationBuilder.CreateIndex(
                name: "IX_PluginSettings_Plugin_Key",
                table: "PluginSettings",
                columns: new[] { "Plugin", "Key" },
                unique: true);
        }
    }
}
