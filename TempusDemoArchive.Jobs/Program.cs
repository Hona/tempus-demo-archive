#if DEBUG
#define PREDETERMINDED_JOB
#endif

using System.Diagnostics;
using TempusDemoArchive.Jobs;
using TempusDemoArchive.Jobs.StvProcessor;

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

var jobs = new IJob[]
{
    new IngestJobList(),
    new DemoProcessorJob()
};

// User select a job
Console.WriteLine("Select a job to run:");
for (var i = 0; i < jobs.Length; i++)
{
    Console.WriteLine($"{i + 1}. {jobs[i].GetType().Name}");
}

#if PREDETERMINDED_JOB
var job = jobs[1];
#else
var jobIndex = int.Parse(Console.ReadLine() ?? "1") - 1;
var job = jobs[jobIndex];
#endif


// Get console cancellationtoken
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (sender, args) =>
{
    args.Cancel = true;
    cts.Cancel();
};

Console.WriteLine($"Running job: {job.GetType().Name}");

var stopwatch = Stopwatch.StartNew();
await job.ExecuteAsync(cts.Token);
stopwatch.Stop();

Console.WriteLine("Done!");