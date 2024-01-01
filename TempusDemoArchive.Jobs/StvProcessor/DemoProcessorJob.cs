using System.Diagnostics;
using System.IO.Compression;
using Humanizer;
using TempusDemoArchive.Persistence.Models.STVs;

namespace TempusDemoArchive.Jobs.StvProcessor;

public class DemoProcessorJob : IJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        await using var db = new ArchiveDbContext();
        var httpClient = new HttpClient();

        var unprocessedDemos = db.Demos.Count(x => !x.StvProcessed);

        var counter = 1;
        foreach (var demoEntry in db.Demos.Where(x => !x.StvProcessed))
        {
            var stopwatch = Stopwatch.StartNew();
            Console.WriteLine($"Processing demo {counter} of {unprocessedDemos} (ID: {demoEntry.Id})");
            
            var filePath = ArchivePath.GetDemoFilePath(demoEntry.Id);
            
            var downloadResponse = await httpClient.GetAsync(demoEntry.Url, cancellationToken);

            Console.WriteLine("Downloading demo: " + downloadResponse.Content.Headers.ContentLength.GetValueOrDefault().Bytes());
            
            await using var downloadStream = await downloadResponse.Content.ReadAsStreamAsync(cancellationToken);
            
            using (var zip = new ZipArchive(downloadStream) )
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
            
            counter++;
        }
    }
}