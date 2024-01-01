namespace TempusDemoArchive.Jobs;

public class InfoJob : IJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var db = new ArchiveDbContext();
        
        var processedCount = db.Demos.Count(x => x.StvProcessed);
        var unprocessedCount = db.Demos.Count(x => !x.StvProcessed);
        
        Console.WriteLine($"Processed: {processedCount}");
        Console.WriteLine($"Unprocessed: {unprocessedCount}");
    }
}