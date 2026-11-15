using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bindery.Host.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBookSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Follow",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "BookSources",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BookId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Plugin = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    NormalizedUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    SourceId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    AddedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastFetchedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookSources_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BookSources_BookId_Plugin_NormalizedUrl",
                table: "BookSources",
                columns: new[] { "BookId", "Plugin", "NormalizedUrl" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BookSources_NormalizedUrl",
                table: "BookSources",
                column: "NormalizedUrl");

            migrationBuilder.CreateIndex(
                name: "IX_BookSources_SourceId",
                table: "BookSources",
                column: "SourceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookSources");

            migrationBuilder.DropColumn(
                name: "Follow",
                table: "Jobs");
        }
    }
}
