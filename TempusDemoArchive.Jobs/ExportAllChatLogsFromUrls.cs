using System.Text.Json;
using TempusDemoArchive.Persistence.Models.STVs;

namespace TempusDemoArchive.Jobs;

public class ExportAllChatLogsFromUrls : IJob
{

    
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine("Input demo urls separated by commas");
        var input = Console.ReadLine();
        var urls = input.Split("\n");
        
        var chatLogs = new List<StvChat>();

        await using var db = new ArchiveDbContext();
        
        foreach (var url in urls)
        {
            var chatLog = await db.Demos.Where(x => x.Url == url)
                .SelectMany(x => x.Stv.Chats)
                .ToListAsync(cancellationToken: cancellationToken);
            chatLogs.AddRange(chatLog);
        }
        
        var fileName = $"all_chatlogs_{FindExactMessage.ToValidFileName(DateTime.Now.ToString("s"))}.txt";
        var filePath = Path.Combine(ArchivePath.TempRoot, fileName);
        
        await File.WriteAllLinesAsync(filePath, chatLogs.Select(x => x.Text), cancellationToken);
        
    }
}