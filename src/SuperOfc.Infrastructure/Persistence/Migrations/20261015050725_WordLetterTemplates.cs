using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SuperOfc.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WordLetterTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "Document",
                table: "LetterTemplate",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DocumentFileName",
                table: "LetterTemplate",
                type: "TEXT",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DocumentSha256",
                table: "LetterTemplate",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasDocument",
                table: "IssuedLetter",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "FatherName",
                table: "Employee",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "IssuedLetterDocument",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    IssuedLetterId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Content = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssuedLetterDocument", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IssuedLetterDocument_IssuedLetter_IssuedLetterId",
                        column: x => x.IssuedLetterId,
                        principalTable: "IssuedLetter",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IssuedLetterDocument_IssuedLetterId",
                table: "IssuedLetterDocument",
                column: "IssuedLetterId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IssuedLetterDocument");

            migrationBuilder.DropColumn(
                name: "Document",
                table: "LetterTemplate");

            migrationBuilder.DropColumn(
                name: "DocumentFileName",
                table: "LetterTemplate");

            migrationBuilder.DropColumn(
                name: "DocumentSha256",
                table: "LetterTemplate");

            migrationBuilder.DropColumn(
                name: "HasDocument",
                table: "IssuedLetter");

            migrationBuilder.DropColumn(
                name: "FatherName",
                table: "Employee");
        }
    }
}
