using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bindery.Host.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReadProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReadProgress",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BookId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TokenId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Progress = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Percentage = table.Column<double>(type: "REAL", nullable: true),
                    Device = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    DeviceId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReadProgress", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReadProgress_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReadProgress_BookId_TokenId",
                table: "ReadProgress",
                columns: new[] { "BookId", "TokenId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReadProgress");
        }
    }
}
