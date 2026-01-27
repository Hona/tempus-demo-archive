using System.Text.Json;

namespace TempusDemoArchive.Jobs;

public class ReparseDemosJob : IJob
{
    private readonly int MaxConcurrentTasks = 3;
    private const int BatchSize = 200;
    private const int DefaultLogEvery = 50;
    private const string StateFileName = "reparse-demos-state.json";

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        using var httpClient = new HttpClient();
        var semaphore = new SemaphoreSlim(MaxConcurrentTasks);
        var logEvery = GetLogEvery();
        var verbose = GetVerbose();
        Console.WriteLine($"Reparse log every: {logEvery}");
        Console.WriteLine($"Reparse verbose: {verbose}");

        var explicitIds = GetExplicitDemoIds();
        if (explicitIds != null)
        {
            var explicitTotal = explicitIds.Count;
            Console.WriteLine($"Total demos to reparse: {explicitTotal}");
            await ProcessBatchesAsync(explicitIds, explicitTotal, 0, httpClient, semaphore, cancellationToken, MaxConcurrentTasks, logEvery, verbose);
            return;
        }

        var state = LoadState();
        var processed = state?.ProcessedCount ?? 0;
        var limit = GetLimit();

        var totalTarget = await GetTotalTargetAsync(limit, cancellationToken);
        Console.WriteLine($"Total demos to reparse: {totalTarget}");
        if (totalTarget == 0)
        {
            return;
        }

        if (state != null)
        {
            Console.WriteLine($"Resuming reparse at offset {processed}");
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

            var batchLimit = Math.Min(BatchSize, remainingLimit);
            var batch = await db.Demos
                .Where(demo => demo.StvProcessed)
                .OrderBy(demo => demo.Id)
                .Select(demo => demo.Id)
                .Skip(processed)
                .Take(batchLimit)
                .ToListAsync(cancellationToken);

            if (batch.Count == 0)
            {
                break;
            }

            processed += await ProcessBatchesAsync(batch, totalTarget, processed, httpClient, semaphore, cancellationToken, MaxConcurrentTasks, logEvery, verbose);
            SaveState(new ReparseState(processed, DateTimeOffset.UtcNow));
        }
    }

    private static async Task<int> ProcessBatchesAsync(IReadOnlyList<ulong> demoIds, int totalTarget, int processedBase,
        HttpClient httpClient, SemaphoreSlim semaphore, CancellationToken cancellationToken, int maxConcurrentTasks,
        int logEvery, bool verbose)
    {
        var counter = 0;
        var tasks = demoIds.Select(async demoId =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var current = Interlocked.Increment(ref counter);
                var totalProcessed = processedBase + current;
                if (logEvery > 0 && (totalProcessed == 1 || totalProcessed % logEvery == 0))
                {
                    var remaining = Math.Max(totalTarget - totalProcessed, 0);
                    Console.WriteLine($"Reparsed {totalProcessed}/{totalTarget} (remaining {remaining})");
                }

                _ = await StvProcessor.ParseDemosJob.ProcessDemoAsync(demoId, httpClient, cancellationToken,
                    forceReparse: true, verbose: verbose);
            }
            catch (Exception e)
            {
                if (StvProcessor.ParseDemosJob.IsCorruptDemo(e))
                {
                    Console.WriteLine("Demo corrupt (invalid STV): " + demoId);
                }
                else
                {
                    Console.WriteLine("Error reparsing demo: " + demoId);
                    Console.WriteLine(e);
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        return demoIds.Count;
    }

    private static async Task<int> GetTotalTargetAsync(int limit, CancellationToken cancellationToken)
    {
        await using var db = new ArchiveDbContext();
        var total = await db.Demos.CountAsync(demo => demo.StvProcessed, cancellationToken);
        return limit > 0 ? Math.Min(limit, total) : total;
    }

    private static List<ulong>? GetExplicitDemoIds()
    {
        var value = EnvVar.GetString("TEMPUS_REPARSE_DEMO_IDS");
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
        return EnvVar.GetInt("TEMPUS_REPARSE_LIMIT", 0, min: 0);
    }

    private static int GetLogEvery()
    {
        return EnvVar.GetPositiveInt("TEMPUS_REPARSE_LOG_EVERY", DefaultLogEvery);
    }

    private static bool GetVerbose()
    {
        return EnvVar.GetBool("TEMPUS_REPARSE_VERBOSE");
    }

    private static ReparseState? LoadState()
    {
        if (EnvVar.GetBool("TEMPUS_REPARSE_RESET"))
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

    private sealed record ReparseState(int ProcessedCount, DateTimeOffset UpdatedAt);
}
