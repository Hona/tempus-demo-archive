using System.IO.Compression;
using TempusDemoArchive.Persistence.Models.STVs;

namespace TempusDemoArchive.Jobs.StvProcessor;

public class DemoProcessorJob : IJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        await using var db = new ArchiveDbContext();
        var httpClient = new HttpClient();

        foreach (var demoEntry in db.Demos.Where(x => !x.StvProcessed)
                     .OrderBy(x => x.Date)) // Oldest -> newest
        {
            var filePath = ArchivePath.GetDemoFilePath(demoEntry.Id);
            
            var downloadStream = await httpClient.GetStreamAsync(demoEntry.Url, cancellationToken);
            using (var zip = new ZipArchive(downloadStream) )
            {
                var entry = zip.Entries.First(x => x.FullName.EndsWith(".dem"));

                using (var sr = new StreamReader(entry.Open()))
                {
                    
                    await using (var sw = File.OpenWrite(filePath))
                    {
                        await sr.BaseStream.CopyToAsync(sw, cancellationToken);
                    }
                }
            }

            var output = StvParser.ExtractStvData(filePath);

            var stv = new Stv
            {
                DemoId = demoEntry.Id,
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
            
            File.Delete(filePath);

            await db.SaveChangesAsync(cancellationToken);
        }
    }
}