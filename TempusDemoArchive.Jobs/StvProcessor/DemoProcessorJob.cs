using System.Diagnostics;
using System.IO.Compression;
using Humanizer;
using TempusDemoArchive.Persistence.Models;
using TempusDemoArchive.Persistence.Models.STVs;

namespace TempusDemoArchive.Jobs.StvProcessor;

public class DemoProcessorJob : IJob
{
    private readonly int MaxConcurrentTasks = 5; // Adjust this value to change the degree of parallelism

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        await using var db = new ArchiveDbContext();
        var httpClient = new HttpClient();
        var semaphore = new SemaphoreSlim(MaxConcurrentTasks);

        var demosToProcess = db.Demos.Where(x => !x.StvProcessed).ToList();
        var unprocessedDemos = demosToProcess.Count;

        int counter = 1;

        var tasks = demosToProcess.Select(async demoEntry =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                Console.WriteLine($"Processing demo {counter} (+- {MaxConcurrentTasks}) of {unprocessedDemos} (ID: {demoEntry.Id})");

                await ProcessDemoAsync(demoEntry.Id, httpClient, cancellationToken);

                counter++;
            }
            catch (Exception e)
            {
                Console.WriteLine("Error processing demo: " + demoEntry.Id);
                Console.WriteLine(e);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    private async Task ProcessDemoAsync(ulong demoId, HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        
        await using var db = new ArchiveDbContext();

        var demoEntry = await db.Demos.FindAsync(demoId, cancellationToken);
        
        if (demoEntry is null)
        {
            Console.WriteLine("Demo not found in database: " + demoId);
            return;
        }

        var filePath = ArchivePath.GetDemoFilePath(demoId);

        var downloadResponse = await httpClient.GetAsync(demoEntry.Url, cancellationToken);

        Console.WriteLine("Downloading demo: " +
                          downloadResponse.Content.Headers.ContentLength.GetValueOrDefault().Bytes());

        await using var downloadStream = await downloadResponse.Content.ReadAsStreamAsync(cancellationToken);

        using (var zip = new ZipArchive(downloadStream))
        {
            var entry = zip.Entries.First(x => x.FullName.EndsWith(".dem"));

            using (var sr = new StreamReader(entry.Open()))
            {
                await using (var sw = File.OpenWrite(filePath))
                {
                    Console.WriteLine("Extracting demo: " + entry.Length.Bytes());
                    await sr.BaseStream.CopyToAsync(sw, cancellationToken);
                }
            }
        }

        Console.WriteLine("Extracting STV data");
        var output = StvParser.ExtractStvData(filePath);

        Console.WriteLine("Mapping to DB model");
        var stv = new Stv
        {
            DemoId = demoEntry.Id,

            DownloadSize = downloadResponse.Content.Headers.ContentLength ?? 0,
            ExtractedFileSize = new FileInfo(filePath).Length,

            IntervalPerTick = output.IntervalPerTick,
            StartTick = output.StartTick,
            Header = new StvHeader
            {
                DemoType = output.Header.DemoType,
                Duration = output.Header.Duration,
                Frames = output.Header.Frames,
                Game = output.Header.Game,
                Map = output.Header.Map,
                Nick = output.Header.Nick,
                Protocol = output.Header.Protocol,
                Server = output.Header.Server,
                Signon = output.Header.Signon,
                Ticks = output.Header.Ticks,
                Version = output.Header.Version
            },
            Chats = output.Chat
                .Where(x => x.Text != string.Empty && x.Text != "0")
                .Select((x, i) => new StvChat
                {
                    DemoId = demoEntry.Id,
                    From = x.From,
                    Kind = x.Kind,
                    Text = x.Text,
                    Tick = x.Tick,
                    Index = i
                }).ToList(),
            Users = output.Users.Select(x => new StvUser
            {
                DemoId = demoEntry.Id,
                Name = x.Value.Name,
                SteamId = x.Value.SteamId,
                Team = x.Value.Team,
                UserId = x.Value.UserId
            }).ToList()
        };

        db.Add(stv);
        demoEntry.StvProcessed = true;

        Console.WriteLine("Deleting demo file");

        File.Delete(filePath);

        Console.WriteLine("Saving changes");

        await db.SaveChangesAsync(cancellationToken);
        Console.WriteLine("Done. Took " + stopwatch.Elapsed.Humanize(2) + "\n");
    }
}