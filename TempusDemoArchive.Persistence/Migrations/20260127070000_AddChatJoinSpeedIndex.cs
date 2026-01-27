using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TempusDemoArchive.Persistence.Migrations
{
    [DbContext(typeof(ArchiveDbContext))]
    [Migration("20260127070000_AddChatJoinSpeedIndex")]
    public partial class AddChatJoinSpeedIndex : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS IX_StvChats_DemoId_FromUserId ON StvChats(DemoId, FromUserId) WHERE FromUserId IS NOT NULL");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_StvChats_DemoId_FromUserId");
        }
    }
}
