/*#define PRESELECTED_JOB*/

using System.Diagnostics;
using Humanizer;
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

#if PRESELECTED_JOB
var job = new SentimentAnalysis_PlayerSpecificJob();
#else
var jobs = new IJob[]
{
    new IngestJobList(),
    new CrawlRecordDemosJob(),
    new DemoProcessorJob(),
    new ReparseProcessedDemosJob(),
    new InfoJob(),
    new FixupProcessedItemsJob(),
    new ListUnprocessedDemos(),
    new TESTINGWrHistoryJob(),
    new ExportAllChatLogsFromMap(),
    new MapNamesThatContain(),
    new FirstBrazilWr(),
    new ExportAllChatLogsFromUrls(),
    new FindExactMessage(),
    new GetUserChatLogs(),
    new RankNaughtyWords(),
    new SentimentAnalysisJob()
};

// User select a job
Console.WriteLine("Select a job to run:");
for (var i = 0; i < jobs.Length; i++)
{
    Console.WriteLine($"{i + 1}. {jobs[i].GetType().Name}");
}

var jobIndex = int.Parse(Console.ReadLine() ?? "1") - 1;
var job = jobs[jobIndex];
#endif

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
