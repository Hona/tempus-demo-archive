using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TempusDemoArchive.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Stv : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Stv",
                columns: table => new
                {
                    DemoId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Header_DemoType = table.Column<string>(type: "TEXT", nullable: false),
                    Header_Version = table.Column<int>(type: "INTEGER", nullable: true),
                    Header_Protocol = table.Column<int>(type: "INTEGER", nullable: true),
                    Header_Server = table.Column<string>(type: "TEXT", nullable: false),
                    Header_Nick = table.Column<string>(type: "TEXT", nullable: false),
                    Header_Map = table.Column<string>(type: "TEXT", nullable: false),
                    Header_Game = table.Column<string>(type: "TEXT", nullable: false),
                    Header_Duration = table.Column<double>(type: "REAL", nullable: true),
                    Header_Ticks = table.Column<int>(type: "INTEGER", nullable: true),
                    Header_Frames = table.Column<int>(type: "INTEGER", nullable: true),
                    Header_Signon = table.Column<int>(type: "INTEGER", nullable: true),
                    StartTick = table.Column<int>(type: "INTEGER", nullable: true),
                    IntervalPerTick = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Stv", x => x.DemoId);
                    table.ForeignKey(
                        name: "FK_Stv_Demos_DemoId",
                        column: x => x.DemoId,
                        principalTable: "Demos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StvChat",
                columns: table => new
                {
                    DemoId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Index = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    From = table.Column<string>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    Tick = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StvChat", x => new { x.DemoId, x.Index });
                    table.ForeignKey(
                        name: "FK_StvChat_Stv_DemoId",
                        column: x => x.DemoId,
                        principalTable: "Stv",
                        principalColumn: "DemoId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StvUser",
                columns: table => new
                {
                    DemoId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    SteamId = table.Column<string>(type: "TEXT", nullable: false),
                    Team = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StvUser", x => new { x.DemoId, x.UserId });
                    table.ForeignKey(
                        name: "FK_StvUser_Stv_DemoId",
                        column: x => x.DemoId,
                        principalTable: "Stv",
                        principalColumn: "DemoId",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StvChat");

            migrationBuilder.DropTable(
                name: "StvUser");

            migrationBuilder.DropTable(
                name: "Stv");
        }
    }
}
