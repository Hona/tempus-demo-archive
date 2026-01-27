using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using Humanizer;
using TempusDemoArchive.Persistence.Models;
using TempusDemoArchive.Persistence.Models.STVs;

namespace TempusDemoArchive.Jobs.StvProcessor;

public class ParseDemosJob : IJob
{
    private const int DefaultParallelism = 5;
    private const int DefaultBatchSize = 200;
    private const int DefaultLogEvery = 50;
    private const long SteamId64Base = 76561197960265728;
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss'Z'";

    internal readonly record struct ParseOutcome(int ChatCount);

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var parallelism = GetParallelism();
        var batchSize = GetBatchSize();
        var logEvery = GetLogEvery();
        var verbose = GetVerbose();
        var includeFailed = GetIncludeFailed();
        Log($"Parse parallelism: {parallelism}");
        Log($"Parse batch size: {batchSize}");
        Log($"Parse log every: {logEvery}");
        Log($"Parse verbose: {verbose}");
        Log($"Parse include failed: {includeFailed}");

        var httpClient = new HttpClient();
        var semaphore = new SemaphoreSlim(parallelism);
        var completedCount = 0L;
        var successCount = 0L;
        var failedCount = 0L;
        var corruptCount = 0L;
        var totalChatMessages = 0L;
        var runStopwatch = Stopwatch.StartNew();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var db = new ArchiveDbContext();
            var pendingQuery = db.Demos.Where(x => !x.StvProcessed);
            if (!includeFailed)
            {
                pendingQuery = pendingQuery.Where(x => !x.StvFailed);
            }

            var remaining = await pendingQuery.CountAsync(cancellationToken);
            if (remaining == 0)
            {
                break;
            }

            var demoIds = await pendingQuery
                .OrderBy(x => x.Date)
                .ThenBy(x => x.Id)
                .Select(x => x.Id)
                .Take(batchSize)
                .ToListAsync(cancellationToken);

            if (demoIds.Count == 0)
            {
                break;
            }

            var batchCompletedStart = Interlocked.Read(ref completedCount);
            var batchSuccessStart = Interlocked.Read(ref successCount);
            var batchFailedStart = Interlocked.Read(ref failedCount);
            var batchCorruptStart = Interlocked.Read(ref corruptCount);
            var batchMessagesStart = Interlocked.Read(ref totalChatMessages);
            var tasks = demoIds.Select(async demoId =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    var outcome = await ProcessDemoAsync(demoId, httpClient, cancellationToken, forceReparse: false,
                        verbose: verbose);
                    Interlocked.Increment(ref successCount);
                    if (outcome.ChatCount > 0)
                    {
                        Interlocked.Add(ref totalChatMessages, outcome.ChatCount);
                    }
                }
                catch (Exception e)
                {
                    if (IsCorruptDemo(e))
                    {
                        Interlocked.Increment(ref corruptCount);
                        Log("Demo corrupt (invalid STV): " + demoId);
                        await MarkDemoFailedAsync(demoId, "invalid_stv", cancellationToken);
                    }
                    else
                    {
                        Interlocked.Increment(ref failedCount);
                        Log("Error processing demo: " + demoId);
                        LogException(e);
                    }
                }
                finally
                {
                    var completed = Interlocked.Increment(ref completedCount);
                    if (logEvery > 0 && (completed == 1 || completed % logEvery == 0))
                    {
                        var successes = Interlocked.Read(ref successCount);
                        var failures = Interlocked.Read(ref failedCount);
                        var corrupt = Interlocked.Read(ref corruptCount);
                        var remainingEstimate = Math.Max(remaining - (successes - batchSuccessStart) - (corrupt - batchCorruptStart), 0);
                        Log($"Processed {completed} demos (ok {successes}, corrupt {corrupt}, failed {failures}, remaining ~{remainingEstimate})");
                    }
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
            var remainingAfterBatch = await pendingQuery.CountAsync(cancellationToken);
            var completedAfterBatch = Interlocked.Read(ref completedCount);
            var successAfterBatch = Interlocked.Read(ref successCount);
            var failedAfterBatch = Interlocked.Read(ref failedCount);
            var corruptAfterBatch = Interlocked.Read(ref corruptCount);
            var messagesAfterBatch = Interlocked.Read(ref totalChatMessages);
            var batchCompleted = completedAfterBatch - batchCompletedStart;
            var batchSuccess = successAfterBatch - batchSuccessStart;
            var batchFailed = failedAfterBatch - batchFailedStart;
            var batchCorrupt = corruptAfterBatch - batchCorruptStart;
            var batchMessages = messagesAfterBatch - batchMessagesStart;
            var elapsed = runStopwatch.Elapsed;
            var summary =
                $"Batch done: +{batchCompleted} demos (ok {batchSuccess}, corrupt {batchCorrupt}, failed {batchFailed}), +{batchMessages:N0} messages (total {messagesAfterBatch:N0}), remaining {remainingAfterBatch:N0}";
            if (successAfterBatch > 0)
            {
                var avgSeconds = elapsed.TotalSeconds / successAfterBatch;
                var eta = TimeSpan.FromSeconds(avgSeconds * remainingAfterBatch);
                summary += $", avg {avgSeconds:0.00}s/demo, ETA {eta.Humanize(2)}";
            }

            if (corruptAfterBatch > 0 || failedAfterBatch > 0)
            {
                summary += $", corrupt total {corruptAfterBatch:N0}, failed total {failedAfterBatch:N0}";
            }

            Log(summary);
        }
    }

    private static void Log(string message)
    {
        Console.WriteLine($"[{DateTime.UtcNow.ToString(TimestampFormat, CultureInfo.InvariantCulture)}] {message}");
    }

    private static void LogException(Exception exception)
    {
        var prefix = $"[{DateTime.UtcNow.ToString(TimestampFormat, CultureInfo.InvariantCulture)}] ";
        foreach (var line in exception.ToString().Split(Environment.NewLine))
        {
            Console.WriteLine(prefix + line);
        }
    }

    private static async Task MarkDemoFailedAsync(ulong demoId, string reason, CancellationToken cancellationToken)
    {
        await using var db = new ArchiveDbContext();
        var demoEntry = await db.Demos.FindAsync(new object[] { demoId }, cancellationToken);
        if (demoEntry is null || demoEntry.StvProcessed)
        {
            return;
        }

        demoEntry.StvFailed = true;
        demoEntry.StvFailureReason = reason;
        await db.SaveChangesAsync(cancellationToken);
    }

    internal static bool IsCorruptDemo(Exception exception)
    {
        if (exception is InvalidOperationException
            && exception.Message.Contains("STV was invalid", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (exception is AggregateException aggregate)
        {
            return aggregate.InnerExceptions.Any(IsCorruptDemo);
        }

        return exception.InnerException != null && IsCorruptDemo(exception.InnerException);
    }

    private static int GetParallelism()
    {
        return EnvVar.GetPositiveInt("TEMPUS_PARSE_PARALLELISM", DefaultParallelism);
    }

    private static int GetBatchSize()
    {
        return EnvVar.GetPositiveInt("TEMPUS_PARSE_BATCH_SIZE", DefaultBatchSize);
    }

    private static int GetLogEvery()
    {
        return EnvVar.GetPositiveInt("TEMPUS_PARSE_LOG_EVERY", DefaultLogEvery);
    }

    private static bool GetVerbose()
    {
        return EnvVar.GetBool("TEMPUS_PARSE_VERBOSE");
    }

    private static bool GetIncludeFailed()
    {
        return EnvVar.GetBool("TEMPUS_PARSE_INCLUDE_FAILED");
    }

    internal static async Task<ParseOutcome> ProcessDemoAsync(ulong demoId, HttpClient httpClient,
        CancellationToken cancellationToken, bool forceReparse, bool verbose)
    {
        var stopwatch = Stopwatch.StartNew();
        
        await using var db = new ArchiveDbContext();

        var demoEntry = await db.Demos.FindAsync(demoId, cancellationToken);
        
        if (demoEntry is null)
        {
            Log("Demo not found in database: " + demoId);
            return new ParseOutcome(0);
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

        var downloadResponse = await httpClient.GetAsync(demoEntry.Url, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        downloadResponse.EnsureSuccessStatusCode();

        var downloadSizeBytes = downloadResponse.Content.Headers.ContentLength;

        if (verbose)
        {
            Log("Downloading demo: " + downloadSizeBytes.GetValueOrDefault().Bytes());
        }

        await using var downloadStream = await downloadResponse.Content.ReadAsStreamAsync(cancellationToken);

        using (var zip = new ZipArchive(downloadStream, ZipArchiveMode.Read))
        {
            var entry = zip.Entries.First(x => x.FullName.EndsWith(".dem"));
            await using var entryStream = entry.Open();
            await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None,
                81920, FileOptions.SequentialScan);
            if (verbose)
            {
                Log("Extracting demo: " + entry.Length.Bytes());
            }
            await entryStream.CopyToAsync(fileStream, cancellationToken);
        }

        if (verbose)
        {
            Log("Extracting STV data");
        }
        var output = StvParser.ExtractStvData(filePath);

        if (verbose)
        {
            Log("Mapping to DB model");
        }
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
        demoEntry.StvFailed = false;
        demoEntry.StvFailureReason = null;
        var chatCount = stv.Chats.Count;

        if (verbose)
        {
            Log("Deleting demo file");
        }

        File.Delete(filePath);

        if (verbose)
        {
            Log("Saving changes");
        }

        await db.SaveChangesAsync(cancellationToken);
        if (verbose)
        {
            Log("Done. Took " + stopwatch.Elapsed.Humanize(2));
        }

        return new ParseOutcome(chatCount);
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
