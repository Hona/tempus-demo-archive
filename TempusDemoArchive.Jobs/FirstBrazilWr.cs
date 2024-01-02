namespace TempusDemoArchive.Jobs;

public class FirstBrazilWr : IJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        // So he wants his first WR he got. On a brazil server 'jump.tf (Brazil)...'
        
        await using var db = new ArchiveDbContext();
        
        var brazilServer = "jump.tf (Brasil)";
        
        var brazilStvs = db.Stvs
            .Where(x => x.Header.Server.Contains(brazilServer));
        
        var brazilWrMessages = brazilStvs
            .SelectMany(x => x.Chats)
            .Select(x => new {Chat = x, x.DemoId})
            .Where(x => x.Chat.Text.Contains("broke"));
        
        
        var brazilWrMessagesList = await brazilWrMessages.ToListAsync(cancellationToken);
        
        foreach (var tuple in brazilWrMessagesList)
        {
            Console.WriteLine(tuple.Chat.Text);
        }
    }
}