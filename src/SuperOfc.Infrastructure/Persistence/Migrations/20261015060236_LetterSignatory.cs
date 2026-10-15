using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SuperOfc.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LetterSignatory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SignatoryName",
                table: "Company",
                type: "TEXT",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SignatoryTitle",
                table: "Company",
                type: "TEXT",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SignatoryName",
                table: "Company");

            migrationBuilder.DropColumn(
                name: "SignatoryTitle",
                table: "Company");
        }
    }
}
