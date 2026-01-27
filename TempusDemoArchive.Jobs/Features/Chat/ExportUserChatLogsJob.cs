using System.Text;

namespace TempusDemoArchive.Jobs;

public class ExportUserChatLogsJob : IJob
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

        var userQuery = ArchiveQueries.SteamUserQuery(db, steamId);

        var matching = await ArchiveQueries.ChatsWithUsers(db)
            .Join(userQuery,
                chat => new { chat.DemoId, chat.SteamId64, SteamId = chat.SteamId ?? string.Empty },
                user => new { user.DemoId, user.SteamId64, SteamId = (string?)user.SteamIdClean ?? user.SteamId },
                (chat, _) => chat)
            .ToListAsync(cancellationToken);
        
        var stringBuilder = new StringBuilder();
        
        stringBuilder.AppendLine("Matches found");
        
        foreach (var match in matching)
        {
            var demo = await db.Demos.FirstOrDefaultAsync(x => x.Id == match.DemoId, cancellationToken: cancellationToken);
            var date = demo == null ? "unknown" : ArchiveUtils.FormatDate(ArchiveUtils.GetDateFromTimestamp(demo.Date));
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
