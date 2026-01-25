using TempusApi;
using TempusApi.Enums;
using TempusApi.Models.Activity;
using TempusApi.Models.Responses;
using ResponseZoneInfo = TempusApi.Models.Responses.ZoneInfo;
using TempusDemoArchive.Persistence.Models;

namespace TempusDemoArchive.Jobs;

public class CrawlRecordDemosJob : IJob
{
    private const int PageSize = 50;
    private const bool UseUnlimitedLimit = true;

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        await using var db = new ArchiveDbContext();
        db.ChangeTracker.AutoDetectChangesEnabled = false;

        var existingIds = db.Demos.Select(x => x.Id).ToHashSet();
        Console.WriteLine($"Existing demos: {existingIds.Count}");

        using var httpClient = new HttpClient();
        var client = new TempusClient(httpClient);

        var mapIds = await GetMapIdsAsync(client, cancellationToken);
        Console.WriteLine($"Maps: {mapIds.Count}");

        var newDemos = new List<Demo>();
        var totalAdded = 0;
        var zoneIdsSeen = new HashSet<long>();
        var mapIndex = 0;

        foreach (var mapId in mapIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            mapIndex++;
            Console.WriteLine($"Map {mapIndex}/{mapIds.Count}: {mapId}");

            var zoneIds = await GetZoneIdsAsync(client, mapId, cancellationToken);
            foreach (var zoneId in zoneIds)
            {
                if (!zoneIdsSeen.Add(zoneId))
                {
                    continue;
                }

                await foreach (var demoEntry in EnumerateZoneDemosAsync(client, zoneId, cancellationToken))
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

                    if (newDemos.Count < 500)
                    {
                        continue;
                    }

                    totalAdded += await PersistDemosAsync(db, newDemos, cancellationToken);
                }
            }

            totalAdded += await PersistDemosAsync(db, newDemos, cancellationToken);
        }

        totalAdded += await PersistDemosAsync(db, newDemos, cancellationToken);
        Console.WriteLine($"New demos added: {totalAdded}");
    }

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

    private async IAsyncEnumerable<DemoEntry> EnumerateZoneDemosAsync(TempusClient client, long zoneId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var start = 1;
        string? previousPageKey = null;
        var useUnlimited = UseUnlimitedLimit;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ZonedRecordsModel records;
            try
            {
                records = await client.GetTopZonedTimes(zoneId, useUnlimited ? 0 : PageSize, start, cancellationToken: cancellationToken);
            }
            catch (HttpRequestException)
            {
                yield break;
            }

            if (records.Runs == null)
            {
                yield break;
            }

            var pageKey = GetPageKey(records.Runs);
            if (pageKey == previousPageKey)
            {
                yield break;
            }

            previousPageKey = pageKey;

            var recordCount = GetRecordCount(records.Runs);
            foreach (var demoEntry in ExtractDemoEntries(records.Runs))
            {
                yield return demoEntry;
            }

            if (useUnlimited)
            {
                if (recordCount < PageSize || recordCount > PageSize)
                {
                    yield break;
                }

                useUnlimited = false;
            }
            else if (recordCount < PageSize)
            {
                yield break;
            }

            start += PageSize;
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

    private static int GetRecordCount(ZonedResults results)
    {
        var soldierCount = results.SoldierRuns?.Count ?? 0;
        var demomanCount = results.DemomanRuns?.Count ?? 0;
        return soldierCount + demomanCount;
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
}
