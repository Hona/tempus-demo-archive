using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TempusDemoArchive.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UpdateDbSet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Stv_Demos_DemoId",
                table: "Stv");

            migrationBuilder.DropForeignKey(
                name: "FK_StvChat_Stv_DemoId",
                table: "StvChat");

            migrationBuilder.DropForeignKey(
                name: "FK_StvUser_Stv_DemoId",
                table: "StvUser");

            migrationBuilder.DropPrimaryKey(
                name: "PK_StvUser",
                table: "StvUser");

            migrationBuilder.DropPrimaryKey(
                name: "PK_StvChat",
                table: "StvChat");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Stv",
                table: "Stv");

            migrationBuilder.RenameTable(
                name: "StvUser",
                newName: "StvUsers");

            migrationBuilder.RenameTable(
                name: "StvChat",
                newName: "StvChats");

            migrationBuilder.RenameTable(
                name: "Stv",
                newName: "Stvs");

            migrationBuilder.AddPrimaryKey(
                name: "PK_StvUsers",
                table: "StvUsers",
                columns: new[] { "DemoId", "UserId" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_StvChats",
                table: "StvChats",
                columns: new[] { "DemoId", "Index" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_Stvs",
                table: "Stvs",
                column: "DemoId");

            migrationBuilder.AddForeignKey(
                name: "FK_StvChats_Stvs_DemoId",
                table: "StvChats",
                column: "DemoId",
                principalTable: "Stvs",
                principalColumn: "DemoId",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Stvs_Demos_DemoId",
                table: "Stvs",
                column: "DemoId",
                principalTable: "Demos",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_StvUsers_Stvs_DemoId",
                table: "StvUsers",
                column: "DemoId",
                principalTable: "Stvs",
                principalColumn: "DemoId",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_StvChats_Stvs_DemoId",
                table: "StvChats");

            migrationBuilder.DropForeignKey(
                name: "FK_Stvs_Demos_DemoId",
                table: "Stvs");

            migrationBuilder.DropForeignKey(
                name: "FK_StvUsers_Stvs_DemoId",
                table: "StvUsers");

            migrationBuilder.DropPrimaryKey(
                name: "PK_StvUsers",
                table: "StvUsers");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Stvs",
                table: "Stvs");

            migrationBuilder.DropPrimaryKey(
                name: "PK_StvChats",
                table: "StvChats");

            migrationBuilder.RenameTable(
                name: "StvUsers",
                newName: "StvUser");

            migrationBuilder.RenameTable(
                name: "Stvs",
                newName: "Stv");

            migrationBuilder.RenameTable(
                name: "StvChats",
                newName: "StvChat");

            migrationBuilder.AddPrimaryKey(
                name: "PK_StvUser",
                table: "StvUser",
                columns: new[] { "DemoId", "UserId" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_Stv",
                table: "Stv",
                column: "DemoId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_StvChat",
                table: "StvChat",
                columns: new[] { "DemoId", "Index" });

            migrationBuilder.AddForeignKey(
                name: "FK_Stv_Demos_DemoId",
                table: "Stv",
                column: "DemoId",
                principalTable: "Demos",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_StvChat_Stv_DemoId",
                table: "StvChat",
                column: "DemoId",
                principalTable: "Stv",
                principalColumn: "DemoId",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_StvUser_Stv_DemoId",
                table: "StvUser",
                column: "DemoId",
                principalTable: "Stv",
                principalColumn: "DemoId",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
