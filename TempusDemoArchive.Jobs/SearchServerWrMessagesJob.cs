namespace TempusDemoArchive.Jobs;

public class SearchServerWrMessagesJob : IJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        await using var db = new ArchiveDbContext();

        Console.WriteLine("Enter server name substring:");
        var serverFilter = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(serverFilter))
        {
            Console.WriteLine("No server filter provided.");
            return;
        }

        var serverStvs = db.Stvs
            .Where(x => x.Header.Server.Contains(serverFilter));
        
        var serverWrMessages = serverStvs
            .SelectMany(x => x.Chats)
            .Select(x => new {Chat = x, x.DemoId})
            .Where(x => x.Chat.Text.Contains("broke"));
        

        var brazilWrMessagesList = await serverWrMessages.ToListAsync(cancellationToken);
        
        foreach (var tuple in brazilWrMessagesList)
        {
            Console.WriteLine(tuple.Chat.Text);
        }
    }
}
