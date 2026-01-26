namespace TempusDemoArchive.Jobs;

public class ListFailedDemosJob : IJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        await using var db = new ArchiveDbContext();
        var failedDemos = db.Demos
            .Where(x => x.StvFailed)
            .OrderBy(x => x.Id)
            .ToList();

        foreach (var demo in failedDemos)
        {
            var reason = string.IsNullOrWhiteSpace(demo.StvFailureReason)
                ? "unknown"
                : demo.StvFailureReason;
            Console.WriteLine($"{demo.Id} - {reason} - {demo.Url}");
        }
    }
}
