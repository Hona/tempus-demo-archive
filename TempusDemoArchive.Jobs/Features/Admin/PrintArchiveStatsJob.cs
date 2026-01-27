using Humanizer;

namespace TempusDemoArchive.Jobs;

public class PrintArchiveStatsJob : IJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var db = new ArchiveDbContext();
        
        var processedCount = db.Demos.Count(x => x.StvProcessed);
        var failedCount = db.Demos.Count(x => x.StvFailed);
        var unprocessedCount = db.Demos.Count(x => !x.StvProcessed && !x.StvFailed);
        
        Console.WriteLine($"Processed: {processedCount}");
        Console.WriteLine($"Failed: {failedCount}");
        Console.WriteLine($"Unprocessed: {unprocessedCount}");
        
        var totalDownloadedBytes = db.Stvs.Sum(x => x.DownloadSize);
        var totalProcessedBytes = db.Stvs.Sum(x => x.ExtractedFileSize);
        
        Console.WriteLine($"Total Downloaded: {totalDownloadedBytes.Bytes()}");
        Console.WriteLine($"Total Processed: {totalProcessedBytes.Bytes()}");
        
        var databaseSize = new FileInfo(ArchivePath.Db).Length;
        Console.WriteLine($"Database Size: {databaseSize.Bytes()}");
        
        var demoCount = db.Demos.Count();
        
        Console.WriteLine($"Total Demos: {demoCount}");
    }
}
