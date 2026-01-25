using System.Text.Json;

namespace TempusDemoArchive.Jobs;

public class ReparseDemosJob : IJob
{
    private readonly int MaxConcurrentTasks = 3;
    private const int BatchSize = 200;
    private const string StateFileName = "reparse-demos-state.json";

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        using var httpClient = new HttpClient();
        var semaphore = new SemaphoreSlim(MaxConcurrentTasks);
        var explicitIds = GetExplicitDemoIds();
        if (explicitIds != null)
        {
            await ProcessBatchesAsync(explicitIds, explicitIds.Count, httpClient, semaphore, cancellationToken, MaxConcurrentTasks);
            return;
        }

        var state = LoadState();
        var lastId = state?.LastDemoId ?? 0;
        var processed = state?.ProcessedCount ?? 0;
        var limit = GetLimit();

        if (state != null)
        {
            Console.WriteLine($"Resuming reparse at demo {lastId} (processed {processed})");
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var db = new ArchiveDbContext();
            var remainingLimit = limit > 0 ? Math.Max(limit - processed, 0) : int.MaxValue;
            if (remainingLimit == 0)
            {
                break;
            }

            var lastIdLong = lastId > long.MaxValue ? long.MaxValue : (long)lastId;
            var batch = await db.Demos
                .Where(demo => demo.StvProcessed)
                .Where(demo => EF.Property<long>(demo, "Id") > lastIdLong)
                .OrderBy(demo => EF.Property<long>(demo, "Id"))
                .Select(demo => demo.Id)
                .Take(Math.Min(BatchSize, remainingLimit))
                .ToListAsync(cancellationToken);

            if (batch.Count == 0)
            {
                break;
            }

            processed += await ProcessBatchesAsync(batch, processed + batch.Count, httpClient, semaphore, cancellationToken, MaxConcurrentTasks);
            lastId = batch.Last();
            SaveState(new ReparseState(lastId, processed, DateTimeOffset.UtcNow));
        }
    }

    private static async Task<int> ProcessBatchesAsync(IReadOnlyList<ulong> demoIds, int total,
        HttpClient httpClient, SemaphoreSlim semaphore, CancellationToken cancellationToken, int maxConcurrentTasks)
    {
        var counter = 0;
        var tasks = demoIds.Select(async demoId =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var current = Interlocked.Increment(ref counter);
                Console.WriteLine($"Reparsing demo {current} (+- {maxConcurrentTasks}) of {total} (ID: {demoId})");
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
        return demoIds.Count;
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

    private static ReparseState? LoadState()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("TEMPUS_REPARSE_RESET"), "1",
                StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(StateFilePath))
            {
                File.Delete(StateFilePath);
            }

            return null;
        }

        if (!File.Exists(StateFilePath))
        {
            return null;
        }

        var json = File.ReadAllText(StateFilePath);
        return JsonSerializer.Deserialize<ReparseState>(json);
    }

    private static void SaveState(ReparseState state)
    {
        var json = JsonSerializer.Serialize(state);
        File.WriteAllText(StateFilePath, json);
    }

    private static string StateFilePath => Path.Combine(ArchivePath.Root, StateFileName);

    private sealed record ReparseState(ulong LastDemoId, int ProcessedCount, DateTimeOffset UpdatedAt);
}
