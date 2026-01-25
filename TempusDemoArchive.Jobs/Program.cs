using System.Diagnostics;
using Humanizer;
using TempusDemoArchive.Jobs;

ArchivePath.EnsureAllCreated();

Console.WriteLine("Archive Data Root: " + ArchivePath.Root);
Directory.CreateDirectory(ArchivePath.Root);

Console.WriteLine("Archive Database: " + ArchivePath.Db);

// Create database if it doesn't exist & migrate
await using (var db = new ArchiveDbContext())
{
    Console.WriteLine("Connection String: " + db.Database.GetConnectionString());
    await db.Database.MigrateAsync();
}

var jobDefinitions = JobCatalog.All;
var job = ResolveJob(args, jobDefinitions);
if (job is null)
{
    return;
}

// Get console cancellationtoken
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    cts.Cancel();
};

Console.WriteLine($"Running job: {job.GetType().Name}");

var stopwatch = Stopwatch.StartNew();
await job.ExecuteAsync(cts.Token);
stopwatch.Stop();

Console.WriteLine("Done! Took " + stopwatch.Elapsed.Humanize(2));

static IJob? ResolveJob(string[] args, IReadOnlyList<JobDefinition> jobs)
{
    if (args.Any(arg => string.Equals(arg, "--list", StringComparison.OrdinalIgnoreCase)))
    {
        foreach (var definition in jobs)
        {
            Console.WriteLine($"{definition.Id} - {definition.Description}");
        }

        return null;
    }

    var jobId = GetJobId(args);
    if (!string.IsNullOrWhiteSpace(jobId))
    {
        var match = JobCatalog.Find(jobId);
        if (match == null)
        {
            Console.WriteLine($"Unknown job id: {jobId}");
            return null;
        }

        return match.Factory();
    }

    Console.WriteLine("Select a job to run:");
    for (var i = 0; i < jobs.Count; i++)
    {
        var definition = jobs[i];
        Console.WriteLine($"{i + 1}. {definition.DisplayName} ({definition.Id})");
    }

    if (!int.TryParse(Console.ReadLine(), out var jobIndex))
    {
        Console.WriteLine("Invalid selection.");
        return null;
    }

    jobIndex -= 1;
    if (jobIndex < 0 || jobIndex >= jobs.Count)
    {
        Console.WriteLine("Invalid selection.");
        return null;
    }

    return jobs[jobIndex].Factory();
}

static string? GetJobId(string[] args)
{
    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (string.Equals(arg, "--job", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            return args[i + 1];
        }

        const string prefix = "--job=";
        if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return arg.Substring(prefix.Length);
        }
    }

    return null;
}
