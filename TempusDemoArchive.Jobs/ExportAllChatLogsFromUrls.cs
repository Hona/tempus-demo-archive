using System.Text.Json;
using TempusDemoArchive.Persistence.Models.STVs;

namespace TempusDemoArchive.Jobs;

public class ExportAllChatLogsFromUrls : IJob
{
    private const string AllUrls = @"https://demos-archive.tempus2.xyz/51/d3/auto-20211207-013840-jump_benroads2_a3.zip
https://demos-archive.tempus2.xyz/27/1f/auto-20211207-091553-jump_benroads2_a3.zip
https://demos-archive.tempus2.xyz/49/7a/auto-20211208-061222-jump_benroads2_a3.zip
https://demos-archive.tempus2.xyz/50/e5/auto-20211209-004958-jump_benroads2_a3.zip
https://demos-archive.tempus2.xyz/47/88/auto-20211218-204752-jump_benroads2_a3.zip
https://demos-archive.tempus2.xyz/27/7e/auto-20211219-033829-jump_benroads2_a3.zip
https://demos-archive.tempus2.xyz/27/51/auto-20211219-060215-jump_benroads2_a3.zip
https://demos-archive.tempus2.xyz/27/19/auto-20211219-140558-jump_benroads2_a3.zip
https://demos-archive.tempus2.xyz/27/5b/auto-20211220-064630-jump_benroads2_a3.zip
https://demos-archive.tempus2.xyz/27/e0/auto-20211220-150722-jump_benroads2_a3.zip
https://demos-archive.tempus2.xyz/47/8a/auto-20211221-050524-jump_benroads2_a3.zip
https://demos-archive.tempus2.xyz/47/52/auto-20211222-091807-jump_benroads2_a3.zip";
    
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var urls = AllUrls.Split("\n");
        
        var chatLogs = new List<StvChat>();

        await using var db = new ArchiveDbContext();
        
        foreach (var url in urls)
        {
            var chatLog = await db.Demos.Where(x => x.Url == url)
                .SelectMany(x => x.Stv.Chats)
                .ToListAsync(cancellationToken: cancellationToken);
            chatLogs.AddRange(chatLog);
        }
        
        var fileName = $"all_chatlogs.txt";
        var filePath = Path.Combine(ArchivePath.TempRoot, fileName);
        
        await File.WriteAllLinesAsync(filePath, chatLogs.Select(x => x.Text), cancellationToken);
        
    }
}