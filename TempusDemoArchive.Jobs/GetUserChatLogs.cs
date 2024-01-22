using System.Text;

namespace TempusDemoArchive.Jobs;

public class GetUserChatLogs : IJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine("Enter steam ID in format e.g. 'STEAM_0:0:27790406'");
        var steamId = Console.ReadLine();

        await using var db = new ArchiveDbContext();
        
        var matching = db.StvChats
            .Where(x => x.From == x.Stv.Users.FirstOrDefault(x => x.SteamId == steamId).UserId.ToString())
            .Where(x => !x.Text.StartsWith("Tip |"))
            .ToList();
        
        var stringBuilder = new StringBuilder();
        
        stringBuilder.AppendLine("Matches found");
        
        foreach (var match in matching)
        {
            var demo = await db.Demos.FirstOrDefaultAsync(x => x.Id == match.DemoId, cancellationToken: cancellationToken);
            stringBuilder.AppendLine(TESTINGWrHistoryJob.GetDateFromTimestamp(demo.Date) + ": " +match.Text);
        }
        
        stringBuilder.AppendLine("Total matches: " + matching.Count);
        
        var stringOutput = stringBuilder.ToString();
        
        Console.WriteLine(stringOutput);
        
        var fileName = $"chatlogs_{steamId.Replace(":", "-")}.txt";
        
        var filePath = Path.Combine(ArchivePath.TempRoot, fileName);
        
        await File.WriteAllTextAsync(filePath, stringOutput, cancellationToken);
    }
}