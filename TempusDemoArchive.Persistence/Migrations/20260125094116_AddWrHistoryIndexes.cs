using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TempusDemoArchive.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWrHistoryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Stvs_Header_Map",
                table: "Stvs",
                column: "Header_Map");

            migrationBuilder.CreateIndex(
                name: "IX_Stvs_Header_Server",
                table: "Stvs",
                column: "Header_Server");

            migrationBuilder.CreateIndex(
                name: "IX_StvChats_Text_TempusWr",
                table: "StvChats",
                column: "Text",
                filter: "Text LIKE 'Tempus | (%'");

            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS IX_StvChats_Text_IrcWr ON StvChats(Text) WHERE Text LIKE ':: (%'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Stvs_Header_Map",
                table: "Stvs");

            migrationBuilder.DropIndex(
                name: "IX_Stvs_Header_Server",
                table: "Stvs");

            migrationBuilder.DropIndex(
                name: "IX_StvChats_Text_TempusWr",
                table: "StvChats");

            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_StvChats_Text_IrcWr");
        }
    }
}
