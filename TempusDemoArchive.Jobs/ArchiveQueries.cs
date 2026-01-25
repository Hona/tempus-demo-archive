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
}

public class ChatWithUserRow
{
    public ulong DemoId { get; set; }
    public required string Text { get; set; }
    public required string Name { get; set; }
    public string? SteamId { get; set; }
    public long? SteamId64 { get; set; }
}
