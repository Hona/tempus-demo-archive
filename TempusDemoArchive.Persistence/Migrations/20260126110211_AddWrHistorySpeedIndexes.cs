using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TempusDemoArchive.Persistence.Migrations
{
    public partial class AddWrHistorySpeedIndexes : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS IX_StvChats_Text_IrcTempus ON StvChats(Text) WHERE Text LIKE ':: Tempus -%'");
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS IX_StvChats_Text_MapRunWr ON StvChats(Text) WHERE Text LIKE 'Tempus | (%' AND Text LIKE '% map run %' AND Text LIKE '%WR%'");
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS IX_StvChats_Text_Record ON StvChats(Text) WHERE Text LIKE 'Tempus | (%' AND (Text LIKE '%beat the map record%' OR Text LIKE '%set the first map record%' OR Text LIKE '%broke%' OR Text LIKE '%set Bonus%' OR Text LIKE '%set Course%' OR Text LIKE '%set C%' OR Text LIKE '%is ranked%with time%')");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_StvChats_Text_IrcTempus");
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_StvChats_Text_MapRunWr");
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_StvChats_Text_Record");
        }
    }
}
