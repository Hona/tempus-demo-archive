using TempusDemoArchive.Persistence.Models.STVs;

namespace TempusDemoArchive.Jobs;

public static class ArchiveQueries
{
    public static IQueryable<ChatWithUserRow> ChatsWithUsers(ArchiveDbContext db)
    {
        return db.StvChats
            .Where(chat => chat.FromUserId != null)
            .Join(db.StvUsers.Where(user => user.UserId != null),
                chat => new { chat.DemoId, UserId = chat.FromUserId!.Value },
                user => new { user.DemoId, UserId = user.UserId!.Value },
                (chat, user) => new ChatWithUserRow
                {
                    DemoId = chat.DemoId,
                    UserId = user.UserId!.Value,
                    Text = chat.Text,
                    Name = user.Name,
                    SteamId = user.SteamIdClean ?? user.SteamId,
                    SteamId64 = user.SteamId64
                });
    }

    public static IQueryable<StvUser> SteamUserQuery(ArchiveDbContext db, string identifier)
    {
        var steam64 = long.TryParse(identifier, out var parsed) ? parsed : (long?)null;

        return db.StvUsers
            .Where(user => user.UserId != null)
            .Where(user => user.SteamId == identifier
                           || user.SteamIdClean == identifier
                           || (steam64 != null && user.SteamId64 == steam64));
    }

    public static IQueryable<ChatWithUserRow> ChatsForUser(ArchiveDbContext db, string identifier)
    {
        var userQuery = SteamUserQuery(db, identifier).AsNoTracking();
        return ChatsWithUsers(db)
            .Join(userQuery,
                chat => new { chat.DemoId, UserId = (int?)chat.UserId },
                user => new { user.DemoId, user.UserId },
                (chat, _) => chat);
    }

    public static async Task<Dictionary<ulong, DateTime?>> LoadDemoDatesByIdAsync(ArchiveDbContext db,
        IReadOnlyList<ulong> demoIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<ulong, DateTime?>();
        if (demoIds.Count == 0)
        {
            return result;
        }

        foreach (var chunk in DbChunk.Chunk(demoIds))
        {
            var chunkDemos = await db.Demos
                .AsNoTracking()
                .Where(demo => chunk.Contains(demo.Id))
                .Select(demo => new { demo.Id, demo.Date })
                .ToListAsync(cancellationToken);

            foreach (var demo in chunkDemos)
            {
                result[demo.Id] = ArchiveUtils.GetDateFromTimestamp(demo.Date);
            }
        }

        return result;
    }

    public static IQueryable<StvChatResolution> ChatResolutions(ArchiveDbContext db)
    {
        return db.StvChatResolutions.AsNoTracking();
    }
}

public class ChatWithUserRow
{
    public ulong DemoId { get; set; }
    public int UserId { get; set; }
    public required string Text { get; set; }
    public required string Name { get; set; }
    public string? SteamId { get; set; }
    public long? SteamId64 { get; set; }
}
