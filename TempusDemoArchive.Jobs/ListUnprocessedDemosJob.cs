namespace TempusDemoArchive.Jobs;

public class ListUnprocessedDemosJob : IJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        await using var db = new ArchiveDbContext();
        
        var unprocessedDemos = db.Demos.Where(x => !x.StvProcessed).ToList();
        
        foreach (var demo in unprocessedDemos)
        {
            Console.WriteLine(demo.Id + " - " + demo.Url);
        }
    }
}
