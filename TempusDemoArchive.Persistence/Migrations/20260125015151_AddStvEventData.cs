using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TempusDemoArchive.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStvEventData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EntityId",
                table: "StvUsers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsBot",
                table: "StvUsers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SteamId64",
                table: "StvUsers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SteamIdClean",
                table: "StvUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SteamIdKind",
                table: "StvUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ParsedAtUtc",
                table: "Stvs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParserVersion",
                table: "Stvs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ClientEntityId",
                table: "StvChats",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FromUserId",
                table: "StvChats",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "StvDeaths",
                columns: table => new
                {
                    DemoId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Index = table.Column<int>(type: "INTEGER", nullable: false),
                    Tick = table.Column<int>(type: "INTEGER", nullable: false),
                    Weapon = table.Column<string>(type: "TEXT", nullable: false),
                    VictimUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    KillerUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    AssisterUserId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StvDeaths", x => new { x.DemoId, x.Index });
                    table.ForeignKey(
                        name: "FK_StvDeaths_Stvs_DemoId",
                        column: x => x.DemoId,
                        principalTable: "Stvs",
                        principalColumn: "DemoId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StvPauses",
                columns: table => new
                {
                    DemoId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Index = table.Column<int>(type: "INTEGER", nullable: false),
                    FromTick = table.Column<int>(type: "INTEGER", nullable: false),
                    ToTick = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StvPauses", x => new { x.DemoId, x.Index });
                    table.ForeignKey(
                        name: "FK_StvPauses_Stvs_DemoId",
                        column: x => x.DemoId,
                        principalTable: "Stvs",
                        principalColumn: "DemoId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StvSpawns",
                columns: table => new
                {
                    DemoId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Index = table.Column<int>(type: "INTEGER", nullable: false),
                    Tick = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Class = table.Column<string>(type: "TEXT", nullable: false),
                    Team = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StvSpawns", x => new { x.DemoId, x.Index });
                    table.ForeignKey(
                        name: "FK_StvSpawns_Stvs_DemoId",
                        column: x => x.DemoId,
                        principalTable: "Stvs",
                        principalColumn: "DemoId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StvTeamChanges",
                columns: table => new
                {
                    DemoId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Index = table.Column<int>(type: "INTEGER", nullable: false),
                    Tick = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Team = table.Column<string>(type: "TEXT", nullable: false),
                    OldTeam = table.Column<string>(type: "TEXT", nullable: false),
                    Disconnect = table.Column<bool>(type: "INTEGER", nullable: false),
                    AutoTeam = table.Column<bool>(type: "INTEGER", nullable: false),
                    Silent = table.Column<bool>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StvTeamChanges", x => new { x.DemoId, x.Index });
                    table.ForeignKey(
                        name: "FK_StvTeamChanges_Stvs_DemoId",
                        column: x => x.DemoId,
                        principalTable: "Stvs",
                        principalColumn: "DemoId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StvUsers_SteamId64",
                table: "StvUsers",
                column: "SteamId64");

            migrationBuilder.CreateIndex(
                name: "IX_StvChats_FromUserId",
                table: "StvChats",
                column: "FromUserId");

            migrationBuilder.CreateIndex(
                name: "IX_StvChats_Tick",
                table: "StvChats",
                column: "Tick");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StvDeaths");

            migrationBuilder.DropTable(
                name: "StvPauses");

            migrationBuilder.DropTable(
                name: "StvSpawns");

            migrationBuilder.DropTable(
                name: "StvTeamChanges");

            migrationBuilder.DropIndex(
                name: "IX_StvUsers_SteamId64",
                table: "StvUsers");

            migrationBuilder.DropIndex(
                name: "IX_StvChats_FromUserId",
                table: "StvChats");

            migrationBuilder.DropIndex(
                name: "IX_StvChats_Tick",
                table: "StvChats");

            migrationBuilder.DropColumn(
                name: "EntityId",
                table: "StvUsers");

            migrationBuilder.DropColumn(
                name: "IsBot",
                table: "StvUsers");

            migrationBuilder.DropColumn(
                name: "SteamId64",
                table: "StvUsers");

            migrationBuilder.DropColumn(
                name: "SteamIdClean",
                table: "StvUsers");

            migrationBuilder.DropColumn(
                name: "SteamIdKind",
                table: "StvUsers");

            migrationBuilder.DropColumn(
                name: "ParsedAtUtc",
                table: "Stvs");

            migrationBuilder.DropColumn(
                name: "ParserVersion",
                table: "Stvs");

            migrationBuilder.DropColumn(
                name: "ClientEntityId",
                table: "StvChats");

            migrationBuilder.DropColumn(
                name: "FromUserId",
                table: "StvChats");
        }
    }
}
