using TempusApi;
using System.Net;
using System.Text.Json;
using TempusApi.Enums;
using TempusApi.Models.Activity;
using TempusApi.Models.Responses;
using TempusDemoArchive.Persistence.Models;
using ResponseZoneInfo = TempusApi.Models.Responses.ZoneInfo;

namespace TempusDemoArchive.Jobs;

public class CrawlRecordDemosJob : IJob
{
    private const int PageSize = 50;
    private const bool UseUnlimitedLimit = true;
    private const string StateFileName = "crawl-record-demos-state.json";
    private const int DefaultMinIntervalMs = 200;

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        await using var db = new ArchiveDbContext();
        db.ChangeTracker.AutoDetectChangesEnabled = false;

        var existingIds = db.Demos.Select(x => x.Id).ToHashSet();
        Console.WriteLine($"Existing demos: {existingIds.Count}");

        var state = LoadState();
        var resumeMapId = state?.MapId;
        var resumeZoneId = state?.ZoneId;
        var resumeStart = state?.Start ?? 1;
        var resumeZoneComplete = state?.ZoneComplete ?? false;
        var resumeMapReached = resumeMapId == null;
        var resumeZoneReached = resumeZoneId == null;

        if (state != null)
        {
            Console.WriteLine($"Resuming crawl at map {state.MapId}, zone {state.ZoneId}, start {state.Start}");
        }

        var rateLimitCounter = new RateLimitCounter();
        var minIntervalMs = GetMinIntervalMs();
        using var httpClient = new HttpClient(new RateLimitLoggingHandler(rateLimitCounter));
        var client = new TempusClient(httpClient, new TempusClientOptions
        {
            MinimumRequestInterval = TimeSpan.FromMilliseconds(minIntervalMs)
        });
        Console.WriteLine($"Request interval: {minIntervalMs}ms");

        var mapIds = await GetMapIdsAsync(client, cancellationToken);
        Console.WriteLine($"Maps: {mapIds.Count}");

        var totalAdded = 0;
        var zoneIdsSeen = new HashSet<long>();
        var mapIndex = 0;

        foreach (var mapId in mapIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

                if (!resumeMapReached)
                {
                    if (mapId != resumeMapId)
                    {
                        mapIndex++;
                        continue;
                    }

                    resumeMapReached = true;
                }

                mapIndex++;
                Console.WriteLine($"Map {mapIndex}/{mapIds.Count}: {mapId}");

                var zoneIds = await GetZoneIdsAsync(client, mapId, cancellationToken);
                foreach (var zoneId in zoneIds)
                {
                    if (!zoneIdsSeen.Add(zoneId))
                    {
                        continue;
                    }

                    if (!resumeZoneReached && mapId == resumeMapId)
                    {
                        if (zoneId != resumeZoneId)
                        {
                            continue;
                        }

                        resumeZoneReached = true;
                        if (resumeZoneComplete)
                        {
                            continue;
                        }
                    }

                    var start = mapId == resumeMapId && zoneId == resumeZoneId ? resumeStart : 1;
                    totalAdded += await ProcessZoneAsync(client, mapId, zoneId, start, existingIds, db, cancellationToken);
                }
            }

        Console.WriteLine($"New demos added: {totalAdded}");
        Console.WriteLine($"429 responses: {rateLimitCounter.Count}");
    }

    private static CrawlState? LoadState()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("TEMPUS_CRAWL_RESET"), "1",
                StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(StateFilePath))
            {
                File.Delete(StateFilePath);
            }

            return null;
        }

        if (!File.Exists(StateFilePath))
        {
            return null;
        }

        var json = File.ReadAllText(StateFilePath);
        return JsonSerializer.Deserialize<CrawlState>(json);
    }

    private static void SaveState(CrawlState state)
    {
        var json = JsonSerializer.Serialize(state);
        File.WriteAllText(StateFilePath, json);
    }

    private static string StateFilePath => Path.Combine(ArchivePath.Root, StateFileName);

    private sealed record CrawlState(long MapId, long ZoneId, int Start, bool ZoneComplete, DateTimeOffset UpdatedAt);

    private async Task<int> PersistDemosAsync(ArchiveDbContext db, List<Demo> newDemos, CancellationToken cancellationToken)
    {
        if (newDemos.Count == 0)
        {
            return 0;
        }

        var count = newDemos.Count;
        db.Demos.AddRange(newDemos);
        await db.SaveChangesAsync(cancellationToken);
        newDemos.Clear();
        return count;
    }

    private static async Task<List<long>> GetMapIdsAsync(TempusClient client, CancellationToken cancellationToken)
    {
        var maps = await client.GetMapListAsync(cancellationToken);
        return maps.Select(map => map.Id).ToList();
    }

    private static async Task<List<long>> GetZoneIdsAsync(TempusClient client, long mapId, CancellationToken cancellationToken)
    {
        var overview = await client.GetFullMapOverview2Async(mapId, cancellationToken);
        return EnumerateZones(overview).Select(zone => zone.Id).ToList();
    }

    private async Task<int> ProcessZoneAsync(TempusClient client, long mapId, long zoneId, int start,
        HashSet<ulong> existingIds, ArchiveDbContext db, CancellationToken cancellationToken)
    {
        var currentStart = Math.Max(start, 1);
        string? previousPageKey = null;
        var useUnlimited = UseUnlimitedLimit && currentStart == 1;
        var totalAdded = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ZonedRecordsModel records;
            try
            {
                records = await client.GetTopZonedTimes(zoneId, useUnlimited ? 0 : PageSize, currentStart, cancellationToken: cancellationToken);
            }
            catch (HttpRequestException)
            {
                return totalAdded;
            }

            if (records.Runs == null)
            {
                SaveState(new CrawlState(mapId, zoneId, currentStart, true, DateTimeOffset.UtcNow));
                return totalAdded;
            }

            var pageKey = GetPageKey(records.Runs);
            if (pageKey == previousPageKey)
            {
                SaveState(new CrawlState(mapId, zoneId, currentStart, true, DateTimeOffset.UtcNow));
                return totalAdded;
            }

            previousPageKey = pageKey;
            SaveState(new CrawlState(mapId, zoneId, currentStart, false, DateTimeOffset.UtcNow));

            var (soldierCount, demomanCount) = GetRecordCounts(records.Runs);
            var hasUnlimitedResults = soldierCount > PageSize || demomanCount > PageSize;
            var hasMorePages = soldierCount == PageSize || demomanCount == PageSize;

            var newDemos = new List<Demo>();
            foreach (var demoEntry in ExtractDemoEntries(records.Runs))
            {
                if (!existingIds.Add(demoEntry.Id))
                {
                    continue;
                }

                newDemos.Add(new Demo
                {
                    Id = demoEntry.Id,
                    Url = demoEntry.Url,
                    Date = demoEntry.Date,
                    StvProcessed = false
                });
            }

            totalAdded += await PersistDemosAsync(db, newDemos, cancellationToken);

            if (useUnlimited)
            {
                if (hasUnlimitedResults)
                {
                    SaveState(new CrawlState(mapId, zoneId, currentStart, true, DateTimeOffset.UtcNow));
                    return totalAdded;
                }

                useUnlimited = false;
                if (!hasMorePages)
                {
                    SaveState(new CrawlState(mapId, zoneId, currentStart, true, DateTimeOffset.UtcNow));
                    return totalAdded;
                }
            }
            else if (!hasMorePages)
            {
                SaveState(new CrawlState(mapId, zoneId, currentStart, true, DateTimeOffset.UtcNow));
                return totalAdded;
            }

            currentStart += PageSize;
            SaveState(new CrawlState(mapId, zoneId, currentStart, false, DateTimeOffset.UtcNow));
        }
    }

    private static IEnumerable<DemoEntry> ExtractDemoEntries(ZonedResults results)
    {
        foreach (var record in results.SoldierRuns ?? Enumerable.Empty<RecordInfoShort>())
        {
            var demoInfo = record.DemoInfo;
            if (demoInfo == null || string.IsNullOrWhiteSpace(demoInfo.Url))
            {
                continue;
            }

            yield return new DemoEntry((ulong)demoInfo.Id, demoInfo.Url, record.Date);
        }

        foreach (var record in results.DemomanRuns ?? Enumerable.Empty<RecordInfoShort>())
        {
            var demoInfo = record.DemoInfo;
            if (demoInfo == null || string.IsNullOrWhiteSpace(demoInfo.Url))
            {
                continue;
            }

            yield return new DemoEntry((ulong)demoInfo.Id, demoInfo.Url, record.Date);
        }
    }

    private static (int Soldier, int Demoman) GetRecordCounts(ZonedResults results)
    {
        var soldierCount = results.SoldierRuns?.Count ?? 0;
        var demomanCount = results.DemomanRuns?.Count ?? 0;
        return (soldierCount, demomanCount);
    }

    private static string? GetPageKey(ZonedResults results)
    {
        var first = GetRecordId(results.SoldierRuns, first: true) ??
                    GetRecordId(results.DemomanRuns, first: true);
        var last = GetRecordId(results.DemomanRuns, first: false) ??
                   GetRecordId(results.SoldierRuns, first: false);

        if (first == null && last == null)
        {
            return null;
        }

        return $"{first}-{last}";
    }

    private static long? GetRecordId(IReadOnlyList<RecordInfoShort>? records, bool first)
    {
        if (records == null || records.Count == 0)
        {
            return null;
        }

        var record = first ? records[0] : records[^1];
        return record.Id;
    }

    private readonly record struct DemoEntry(ulong Id, string Url, double Date);

    private static IEnumerable<ResponseZoneInfo> EnumerateZones(FullMapOverview2 overview)
    {
        if (overview?.Zones == null)
        {
            yield break;
        }

        foreach (var zone in overview.Zones.Map ?? Enumerable.Empty<ResponseZoneInfo>()) yield return zone;
        foreach (var zone in overview.Zones.Course ?? Enumerable.Empty<ResponseZoneInfo>()) yield return zone;
        foreach (var zone in overview.Zones.CourseEnd ?? Enumerable.Empty<ResponseZoneInfo>()) yield return zone;
        foreach (var zone in overview.Zones.Bonus ?? Enumerable.Empty<ResponseZoneInfo>()) yield return zone;
        foreach (var zone in overview.Zones.BonusEnd ?? Enumerable.Empty<ResponseZoneInfo>()) yield return zone;
        foreach (var zone in overview.Zones.Trick ?? Enumerable.Empty<ResponseZoneInfo>()) yield return zone;
        foreach (var zone in overview.Zones.MapEnd ?? Enumerable.Empty<ResponseZoneInfo>()) yield return zone;
        foreach (var zone in overview.Zones.Misc ?? Enumerable.Empty<ResponseZoneInfo>()) yield return zone;
    }

    private static int GetMinIntervalMs()
    {
        var value = Environment.GetEnvironmentVariable("TEMPUS_CRAWL_MIN_INTERVAL_MS");
        if (int.TryParse(value, out var parsed) && parsed >= 0)
        {
            return parsed;
        }

        return DefaultMinIntervalMs;
    }

    private sealed class RateLimitCounter
    {
        private long _count;

        public long Count => Interlocked.Read(ref _count);

        public void Record(TimeSpan? retryAfter)
        {
            var current = Interlocked.Increment(ref _count);
            if (current <= 3)
            {
                Console.WriteLine(retryAfter.HasValue
                    ? $"Rate limited (Retry-After: {retryAfter.Value.TotalSeconds:n0}s)"
                    : "Rate limited (Retry-After missing)");
            }
        }
    }

    private sealed class RateLimitLoggingHandler : DelegatingHandler
    {
        private readonly RateLimitCounter _counter;

        public RateLimitLoggingHandler(RateLimitCounter counter)
            : base(new HttpClientHandler())
        {
            _counter = counter;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta
                                 ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow);
                _counter.Record(retryAfter);
            }

            return response;
        }
    }
}
