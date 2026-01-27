using System.Diagnostics;
using Humanizer;
using Spectre.Console;
using TempusDemoArchive.Jobs;

ArchivePath.EnsureAllCreated();

Console.WriteLine("Archive Data Root: " + ArchivePath.Root);
Directory.CreateDirectory(ArchivePath.Root);

Console.WriteLine("Archive Database: " + ArchivePath.Db);

var skipMigrations = EnvVar.GetBool("TEMPUS_SKIP_MIGRATIONS");
Console.WriteLine("Skip migrations: " + skipMigrations);

// Create database if it doesn't exist & migrate
await using (var db = new ArchiveDbContext())
{
    Console.WriteLine("Connection String: " + db.Database.GetConnectionString());
    if (!skipMigrations)
    {
        await db.Database.MigrateAsync();
    }
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
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Category")
            .AddColumn("Id")
            .AddColumn("Description");

        foreach (var definition in jobs.OrderBy(job => job.Category).ThenBy(job => job.Id))
        {
            table.AddRow(definition.Category, definition.Id, definition.Description);
        }

        AnsiConsole.Write(table);

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

    var selection = AnsiConsole.Prompt(
        new SelectionPrompt<JobDefinition>()
            .Title("Select a job to run")
            .PageSize(20)
            .MoreChoicesText("(Move up/down to see more)")
            .AddChoices(jobs.OrderBy(job => job.Category).ThenBy(job => job.DisplayName))
            .UseConverter(job => $"{job.Category} | {job.DisplayName} ({job.Id})"));

    return selection.Factory();
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
