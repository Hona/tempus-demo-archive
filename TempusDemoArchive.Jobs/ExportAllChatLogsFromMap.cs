using TempusDemoArchive.Persistence.Models.STVs;

namespace TempusDemoArchive.Jobs;

public class ExportAllChatLogsFromMap : IJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine("Input Map name: ");
        var mapName = Console.ReadLine()?.Trim();

        if (string.IsNullOrWhiteSpace(mapName))
        {
            Console.WriteLine("No map name provided.");
            return;
        }
        
        await using var db = new ArchiveDbContext();
        var matchingStvs = db.Stvs
            .Where(x => x.Header.Map == mapName)
            .OrderBy(x => x.DemoId)
            .ToList();
        
        var chatLogs = new List<StvChat>();
        
        foreach (var stv in matchingStvs)
        {
            var stvChats = await db.StvChats.Where(x => x.DemoId == stv.DemoId)
                .ToListAsync(cancellationToken);
            
            if (stvChats.Count != 0)
            {
                chatLogs.AddRange(stvChats);
            }
        }
        
        var fileName = $"{mapName}_chatlogs.txt";
        var filePath = Path.Combine(ArchivePath.TempRoot, fileName);
        
        await File.WriteAllLinesAsync(filePath, chatLogs.Select(x => x.Text), cancellationToken);
    }
}
