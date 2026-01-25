using System.Diagnostics;
using System.IO.Compression;
using Humanizer;
using TempusDemoArchive.Persistence.Models;
using TempusDemoArchive.Persistence.Models.STVs;

namespace TempusDemoArchive.Jobs.StvProcessor;

public class ParseDemosJob : IJob
{
    private const int DefaultParallelism = 5;
    private const int DefaultBatchSize = 200;
    private const long SteamId64Base = 76561197960265728;

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var parallelism = GetParallelism();
        var batchSize = GetBatchSize();
        Console.WriteLine($"Parse parallelism: {parallelism}");
        Console.WriteLine($"Parse batch size: {batchSize}");

        var httpClient = new HttpClient();
        var semaphore = new SemaphoreSlim(parallelism);
        var processedCount = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var db = new ArchiveDbContext();
            var remaining = await db.Demos.CountAsync(x => !x.StvProcessed, cancellationToken);
            if (remaining == 0)
            {
                break;
            }

            var demoIds = await db.Demos
                .Where(x => !x.StvProcessed)
                .OrderBy(x => x.Date)
                .ThenBy(x => x.Id)
                .Select(x => x.Id)
                .Take(batchSize)
                .ToListAsync(cancellationToken);

            if (demoIds.Count == 0)
            {
                break;
            }

            var tasks = demoIds.Select(async demoId =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    var current = Interlocked.Increment(ref processedCount);
                    Console.WriteLine($"Processing demo {current} (+- {parallelism}) of {remaining} (ID: {demoId})");

                    await ProcessDemoAsync(demoId, httpClient, cancellationToken, forceReparse: false);
                }
                catch (Exception e)
                {
                    Console.WriteLine("Error processing demo: " + demoId);
                    Console.WriteLine(e);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
        }
    }

    private static int GetParallelism()
    {
        var value = Environment.GetEnvironmentVariable("TEMPUS_PARSE_PARALLELISM");
        if (int.TryParse(value, out var parsed) && parsed > 0)
        {
            return parsed;
        }

        return DefaultParallelism;
    }

    private static int GetBatchSize()
    {
        var value = Environment.GetEnvironmentVariable("TEMPUS_PARSE_BATCH_SIZE");
        if (int.TryParse(value, out var parsed) && parsed > 0)
        {
            return parsed;
        }

        return DefaultBatchSize;
    }

    internal static async Task ProcessDemoAsync(ulong demoId, HttpClient httpClient,
        CancellationToken cancellationToken, bool forceReparse)
    {
        var stopwatch = Stopwatch.StartNew();
        
        await using var db = new ArchiveDbContext();

        var demoEntry = await db.Demos.FindAsync(demoId, cancellationToken);
        
        if (demoEntry is null)
        {
            Console.WriteLine("Demo not found in database: " + demoId);
            return;
        }

        if (forceReparse)
        {
            var existingStv = await db.Stvs.FindAsync(new object[] { demoId }, cancellationToken);
            if (existingStv != null)
            {
                db.Stvs.Remove(existingStv);
                demoEntry.StvProcessed = false;
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        var filePath = ArchivePath.GetDemoFilePath(demoId);

        var downloadResponse = await httpClient.GetAsync(demoEntry.Url, cancellationToken);

        var downloadSizeBytes = downloadResponse.Content.Headers.ContentLength;

        Console.WriteLine("Downloading demo: " +
                          downloadSizeBytes.GetValueOrDefault().Bytes());

        // Skip if 1 MB - probably corrupted
        if (downloadSizeBytes < 1 * 1024 * 1024)
        {
            Console.WriteLine("Skipping demo (<1MB - probably corrupt): " + demoId);
            return;
        }

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
        var entityToUserId = output.Users.Values
            .Where(user => user.EntityId.HasValue && user.UserId.HasValue)
            .GroupBy(user => user.EntityId!.Value)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.First().UserId!.Value);

        var stv = new Stv
        {
            DemoId = demoEntry.Id,

            DownloadSize = downloadSizeBytes ?? 0,
            ExtractedFileSize = new FileInfo(filePath).Length,
            ParsedAtUtc = DateTime.UtcNow,
            ParserVersion = Environment.GetEnvironmentVariable("TF_DEMO_PARSER_VERSION"),

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
                    Index = i,
                    ClientEntityId = x.Client,
                    FromUserId = x.Client.HasValue && entityToUserId.TryGetValue(x.Client.Value, out var userId)
                        ? userId
                        : null
                }).ToList(),
            Users = output.Users.Select(x =>
            {
                var normalized = NormalizeSteamId(x.Value.SteamId);
                return new StvUser
                {
                    DemoId = demoEntry.Id,
                    Name = x.Value.Name,
                    SteamId = x.Value.SteamId,
                    SteamIdClean = normalized.Clean,
                    SteamId64 = normalized.SteamId64,
                    SteamIdKind = normalized.Kind,
                    IsBot = normalized.IsBot,
                    EntityId = x.Value.EntityId,
                    Team = x.Value.Team,
                    UserId = x.Value.UserId
                };
            }).ToList(),
            Spawns = (output.Spawns ?? Array.Empty<Spawn>()).Select((spawn, i) => new StvSpawn
            {
                DemoId = demoEntry.Id,
                Index = i,
                Tick = spawn.Tick,
                UserId = spawn.User,
                Class = spawn.Class,
                Team = spawn.Team
            }).ToList(),
            TeamChanges = (output.TeamChanges ?? Array.Empty<TeamChange>()).Select((change, i) => new StvTeamChange
            {
                DemoId = demoEntry.Id,
                Index = i,
                Tick = change.Tick,
                UserId = change.User,
                Team = change.Team,
                OldTeam = change.OldTeam,
                Disconnect = change.Disconnect,
                AutoTeam = change.AutoTeam,
                Silent = change.Silent,
                Name = change.Name
            }).ToList(),
            Deaths = (output.Deaths ?? Array.Empty<Death>()).Select((death, i) => new StvDeath
            {
                DemoId = demoEntry.Id,
                Index = i,
                Tick = death.Tick ?? 0,
                Weapon = death.Weapon,
                VictimUserId = death.Victim ?? 0,
                KillerUserId = death.Killer ?? 0,
                AssisterUserId = death.Assister
            }).ToList(),
            Pauses = (output.Pauses ?? Array.Empty<Pause>()).Select((pause, i) => new StvPause
            {
                DemoId = demoEntry.Id,
                Index = i,
                FromTick = pause.From,
                ToTick = pause.To
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

    private static (string? Clean, long? SteamId64, string? Kind, bool? IsBot) NormalizeSteamId(string? steamId)
    {
        if (string.IsNullOrEmpty(steamId))
        {
            return (null, null, null, null);
        }

        var clean = steamId.Split('\0')[0];
        if (string.IsNullOrEmpty(clean))
        {
            return (clean, null, "unknown", null);
        }

        if (string.Equals(clean, "BOT", StringComparison.OrdinalIgnoreCase))
        {
            return (clean, null, "bot", true);
        }

        if (clean.StartsWith("STEAM_", StringComparison.OrdinalIgnoreCase))
        {
            var parts = clean.Substring("STEAM_".Length).Split(':');
            if (parts.Length == 3
                && int.TryParse(parts[1], out var y)
                && long.TryParse(parts[2], out var z))
            {
                var steamId64 = SteamId64Base + (z * 2) + y;
                return (clean, steamId64, "steam2", false);
            }

            return (clean, null, "steam2", false);
        }

        if (clean.StartsWith("[U:", StringComparison.OrdinalIgnoreCase) && clean.EndsWith("]", StringComparison.Ordinal))
        {
            var lastColon = clean.LastIndexOf(':');
            if (lastColon > 0 && long.TryParse(clean.Substring(lastColon + 1, clean.Length - lastColon - 2), out var z))
            {
                return (clean, SteamId64Base + z, "steam3", false);
            }

            return (clean, null, "steam3", false);
        }

        return (clean, null, "unknown", null);
    }
}
