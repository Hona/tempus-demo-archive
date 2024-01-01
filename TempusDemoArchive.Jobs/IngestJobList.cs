using TempusDemoArchive.Persistence.Models;

namespace TempusDemoArchive.Jobs;

public class IngestJobList : IJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var dbContext = new ArchiveDbContext();

        var archiveDemoLines = await File.ReadAllLinesAsync(ArchivePath.RawDemoList, cancellationToken);

        // Skip the headers (column names + horizontal divider) & skip the last 2 lines (row count from SQL query & empty)
        foreach (var rawDemoLine in archiveDemoLines.Skip(2).SkipLast(2))
        {
            // Basically a CSV with a pipe delimiter, but don't need complex library
            var parts = rawDemoLine.Split('|');

            var demo = new Demo
            {
                Id = ulong.Parse(parts[0].Trim()),
                Url = parts[2].Trim(),
                Date = double.Parse(parts[1].Trim()),
                StvProcessed = false
            };
            
            dbContext.Demos.Add(demo);
        }
        
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}