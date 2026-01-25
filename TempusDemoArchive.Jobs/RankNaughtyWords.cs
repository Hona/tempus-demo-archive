using System.Text;

namespace TempusDemoArchive.Jobs;

public class RankNaughtyWords : IJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        await using var db = new ArchiveDbContext();

        Console.WriteLine("Input naughty word: ");
        var foundMessage = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(foundMessage))
        {
            Console.WriteLine("No word provided.");
            return;
        }
        
        var matching = await db.StvChats
            .Where(chat => chat.FromUserId != null)
            .Where(chat => EF.Functions.Like(chat.Text, $"%{foundMessage}%"))
            .Join(db.StvUsers.Where(user => user.UserId != null),
                chat => new { chat.DemoId, UserId = chat.FromUserId!.Value },
                user => new { user.DemoId, UserId = user.UserId!.Value },
                (chat, user) => new { chat.Text, user.Name, user.SteamId64, user.SteamIdClean, user.SteamId })
            .ToListAsync(cancellationToken: cancellationToken);

        var sb = new StringBuilder();
        
        foreach (var match in matching
                     .GroupBy(x => new { x.SteamId64, x.SteamIdClean, x.SteamId, x.Name })
                     .OrderByDescending(x => x.Count()))
        {
            var key = match.Key;
            var steamId = key.SteamIdClean ?? key.SteamId ?? "unknown";
            var name = string.IsNullOrWhiteSpace(key.Name) ? "unknown" : key.Name;

            sb.AppendLine($"{name} ({steamId}) : {match.Count()}");
        }
        
        var text = sb.ToString();
        
        Console.WriteLine(text);
        
        var fileName = $"naughty_words_{FindExactMessage.ToValidFileName(foundMessage)}.txt";
        
        var filePath = Path.Combine(ArchivePath.TempRoot, fileName);
        
        await File.WriteAllTextAsync(filePath, text, cancellationToken);
    }
}
