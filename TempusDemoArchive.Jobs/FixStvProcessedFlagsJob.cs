namespace TempusDemoArchive.Jobs;

public class FixStvProcessedFlagsJob : IJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        // There was a bug where the StvProcessed flag was not set to true after processing a demo.
        // This job fixes that by checking if the demo has any STV data and setting the flag to true if it does.
        
        await using var db = new ArchiveDbContext();

        var unprocessedDemos = db.Demos.Where(x => !x.StvProcessed);

        foreach (var unprocessedDemo in unprocessedDemos)
        {
            var stvData = await db.Stvs.FindAsync(unprocessedDemo.Id, cancellationToken);

            if (stvData is not null)
            {
                Console.WriteLine($"Fixing demo {unprocessedDemo.Id}");
                unprocessedDemo.StvProcessed = true;
                unprocessedDemo.StvFailed = false;
                unprocessedDemo.StvFailureReason = null;
            }
            else
            {
                Console.WriteLine($"Demo {unprocessedDemo.Id} has no STV data");
            }
        }
        
        await db.SaveChangesAsync(cancellationToken);
    }
}
