using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shoko.Plugin.Tvdb.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddPersonTranslations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BirthPlace",
                table: "People",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Translations",
                table: "People",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BirthPlace",
                table: "People");

            migrationBuilder.DropColumn(
                name: "Translations",
                table: "People");
        }
    }
}
