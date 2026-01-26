using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TempusDemoArchive.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChatResolutionView : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP VIEW IF EXISTS StvChatResolution;
CREATE VIEW StvChatResolution AS
SELECT
    c.DemoId AS DemoId,
    c.""Index"" AS ChatIndex,
    c.ClientEntityId AS ClientEntityId,
    c.FromUserId AS FromUserId,
    CASE WHEN COUNT(u.UserId) = 1 THEN MAX(u.UserId) ELSE NULL END AS ResolvedUserId,
    COUNT(u.UserId) AS CandidateCount,
    GROUP_CONCAT(u.UserId) AS CandidateUserIdsCsv,
    c.Text AS Text,
    c.Tick AS Tick
FROM StvChats c
LEFT JOIN StvUsers u
    ON u.DemoId = c.DemoId
    AND u.EntityId = c.ClientEntityId
    AND u.UserId IS NOT NULL
GROUP BY c.DemoId, c.""Index"", c.ClientEntityId, c.FromUserId, c.Text, c.Tick;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS StvChatResolution;");
        }
    }
}
