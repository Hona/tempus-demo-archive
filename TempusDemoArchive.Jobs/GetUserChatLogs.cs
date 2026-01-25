using System.Text;

namespace TempusDemoArchive.Jobs;

public class GetUserChatLogs : IJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine("Enter steam ID in format e.g. 'STEAM_0:0:27790406'");
        var steamId = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(steamId))
        {
            Console.WriteLine("No steam ID provided.");
            return;
        }

        await using var db = new ArchiveDbContext();

        var userQuery = db.StvUsers
            .Where(user => user.UserId != null)
            .Where(user => user.SteamId == steamId || user.SteamIdClean == steamId);

        var matching = await db.StvChats
            .Where(chat => chat.FromUserId != null)
            .Join(userQuery,
                chat => new { chat.DemoId, UserId = chat.FromUserId!.Value },
                user => new { user.DemoId, UserId = user.UserId!.Value },
                (chat, _) => chat)
            .ToListAsync(cancellationToken);
        
        var stringBuilder = new StringBuilder();
        
        stringBuilder.AppendLine("Matches found");
        
        foreach (var match in matching)
        {
            var demo = await db.Demos.FirstOrDefaultAsync(x => x.Id == match.DemoId, cancellationToken: cancellationToken);
            var date = demo == null ? "unknown" : TESTINGWrHistoryJob.GetDateFromTimestamp(demo.Date).ToString("yyyy-MM-dd");
            stringBuilder.AppendLine(date + ": " + match.Text);
        }
        
        stringBuilder.AppendLine("Total matches: " + matching.Count);
        
        var stringOutput = stringBuilder.ToString();
        
        Console.WriteLine(stringOutput);
        
        var fileName = $"chatlogs_{steamId.Replace(":", "-")}.txt";
        
        var filePath = Path.Combine(ArchivePath.TempRoot, fileName);
        
        await File.WriteAllTextAsync(filePath, stringOutput, cancellationToken);
    }
}
