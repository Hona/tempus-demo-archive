using System.Text;

namespace TempusDemoArchive.Jobs;

public class ExportUserChatLogsJob : IJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var steamId = JobPrompts.ReadNonEmptyLine("Enter steam ID in format e.g. 'STEAM_0:0:27790406'",
            "No steam ID provided.");
        if (steamId == null)
        {
            return;
        }

        await using var db = new ArchiveDbContext();

        var matching = await ArchiveQueries.ChatsForUser(db, steamId)
            .ToListAsync(cancellationToken);

        var demoIds = matching.Select(x => x.DemoId).Distinct().ToList();
        var demoDates = await ArchiveQueries.LoadDemoDatesByIdAsync(db, demoIds, cancellationToken);
        
        var stringBuilder = new StringBuilder();
        
        stringBuilder.AppendLine("Matches found");
        
        foreach (var match in matching)
        {
            var date = demoDates.TryGetValue(match.DemoId, out var demoDate)
                ? ArchiveUtils.FormatDate(demoDate)
                : "unknown";
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
