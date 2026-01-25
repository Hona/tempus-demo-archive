using TempusDemoArchive.Persistence.Models;

namespace TempusDemoArchive.Jobs;

public class IngestArchivedDemosJob : IJob
{
    private const int BatchSize = 1000;

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = new ArchiveDbContext();
        dbContext.ChangeTracker.AutoDetectChangesEnabled = false;

        var existingIds = dbContext.Demos.Select(x => x.Id).ToHashSet();
        Console.WriteLine($"Existing demos: {existingIds.Count}");

        var newDemos = new List<Demo>();
        var totalAdded = 0;

        try
        {
            var archiveDemoLines = File.ReadLines(ArchivePath.RawDemoList)
                .Skip(2)
                .SkipLast(2);

            foreach (var rawDemoLine in archiveDemoLines)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Basically a CSV with a pipe delimiter, but don't need complex library
                var parts = rawDemoLine.Split('|');
                if (parts.Length < 3)
                {
                    continue;
                }

                if (!ulong.TryParse(parts[0].Trim(), out var id))
                {
                    continue;
                }

                if (!existingIds.Add(id))
                {
                    continue;
                }

                if (!double.TryParse(parts[1].Trim(), out var date))
                {
                    continue;
                }

                newDemos.Add(new Demo
                {
                    Id = id,
                    Url = parts[2].Trim(),
                    Date = date,
                    StvProcessed = false
                });

                if (newDemos.Count < BatchSize)
                {
                    continue;
                }

                totalAdded += await PersistDemosAsync(dbContext, newDemos, cancellationToken);
            }
        }
        finally
        {
            totalAdded += await PersistDemosAsync(dbContext, newDemos, cancellationToken);
        }

        Console.WriteLine($"New demos added: {totalAdded}");
    }

    private static async Task<int> PersistDemosAsync(ArchiveDbContext dbContext, List<Demo> newDemos,
        CancellationToken cancellationToken)
    {
        if (newDemos.Count == 0)
        {
            return 0;
        }

        var count = newDemos.Count;
        dbContext.Demos.AddRange(newDemos);
        await dbContext.SaveChangesAsync(cancellationToken);
        newDemos.Clear();
        return count;
    }
}
