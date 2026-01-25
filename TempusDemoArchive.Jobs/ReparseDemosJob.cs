namespace TempusDemoArchive.Jobs;

public class ReparseDemosJob : IJob
{
    private readonly int MaxConcurrentTasks = 3;

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        await using var db = new ArchiveDbContext();
        var demoIds = GetExplicitDemoIds() ?? GetProcessedDemoIds(db);

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
                await StvProcessor.ParseDemosJob.ProcessDemoAsync(demoId, httpClient, cancellationToken, forceReparse: true);
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

    private static List<ulong> GetProcessedDemoIds(ArchiveDbContext db)
    {
        var limit = GetLimit();
        var query = db.Demos
            .Where(demo => demo.StvProcessed)
            .OrderBy(demo => demo.Date)
            .ThenBy(demo => demo.Id)
            .Select(demo => demo.Id);

        if (limit > 0)
        {
            query = query.Take(limit);
        }

        return query.ToList();
    }

    private static List<ulong>? GetExplicitDemoIds()
    {
        var value = Environment.GetEnvironmentVariable("TEMPUS_REPARSE_DEMO_IDS");
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var ids = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(id => ulong.TryParse(id, out var parsed) ? (ulong?)parsed : null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToList();

        return ids.Count == 0 ? null : ids;
    }

    private static int GetLimit()
    {
        var value = Environment.GetEnvironmentVariable("TEMPUS_REPARSE_LIMIT");
        return int.TryParse(value, out var parsed) ? parsed : 0;
    }
}
