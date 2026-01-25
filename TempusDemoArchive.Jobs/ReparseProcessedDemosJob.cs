namespace TempusDemoArchive.Jobs;

public class ReparseProcessedDemosJob : IJob
{
    private readonly int MaxConcurrentTasks = 3;

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        await using var db = new ArchiveDbContext();
        var demoIds = db.Demos
            .Where(demo => demo.StvProcessed)
            .Select(demo => demo.Id)
            .ToList();

        Console.WriteLine($"Reparsing {demoIds.Count} demos");

        using var httpClient = new HttpClient();
        var semaphore = new SemaphoreSlim(MaxConcurrentTasks);
        var counter = 0;

        var tasks = demoIds.Select(async demoId =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var current = Interlocked.Increment(ref counter);
                Console.WriteLine($"Reparsing demo {current} (+- {MaxConcurrentTasks}) of {demoIds.Count} (ID: {demoId})");
                await StvProcessor.DemoProcessorJob.ProcessDemoAsync(demoId, httpClient, cancellationToken, forceReparse: true);
            }
            catch (Exception e)
            {
                Console.WriteLine("Error reparsing demo: " + demoId);
                Console.WriteLine(e);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
    }
}
