using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace TempusDemoArchive.Jobs;

public class ExtractWrHistoryFromChatJob : IJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var map = JobPrompts.ReadMapName();
        if (map == null)
        {
            return;
        }

        var @class = JobPrompts.ReadClassDs();

        await using var db = new ArchiveDbContext();
        var mapPrefix = map + "_";

        var mapDemos = await db.Stvs
            .AsNoTracking()
            .Where(x => x.Header.Map == map || EF.Functions.Like(x.Header.Map, mapPrefix + "%"))
            .Select(x => new { x.DemoId, x.Header.Map })
            .ToListAsync(cancellationToken);
        var mapByDemoId = mapDemos.ToDictionary(x => x.DemoId, x => x.Map);
        var mapDemoIds = mapDemos.Select(x => x.DemoId).ToList();

        var mapsMatching = mapDemos
            .Select(x => x.Map)
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        foreach (var matchingMap in mapsMatching)
        {
            Console.WriteLine(matchingMap);
        }

        var suspectedWrMessages = new List<WrHistoryChat.ChatCandidate>();
        foreach (var chunk in DbChunk.Chunk(mapDemoIds))
        {
            var chunkChats = await db.StvChats
                .AsNoTracking()
                .Where(chat => chunk.Contains(chat.DemoId))
                .WhereLikelyTempusWrMessage()
                .Select(chat => new { chat.DemoId, chat.Text, chat.FromUserId, chat.Index, chat.Tick })
                .ToListAsync(cancellationToken);

            foreach (var chat in chunkChats)
            {
                if (mapByDemoId.TryGetValue(chat.DemoId, out var mapName))
                {
                    suspectedWrMessages.Add(new WrHistoryChat.ChatCandidate(chat.DemoId, mapName, chat.Text, chat.Index,
                        chat.Tick, chat.FromUserId));
                }
            }
        }

        var suspectedIrcWrMessages = await db.StvChats
            .AsNoTracking()
            .WhereLikelyIrcWrMessage()
            .Where(chat => EF.Functions.Like(chat.Text, "% " + map + "%")
                           || EF.Functions.Like(chat.Text, "% " + mapPrefix + "%"))
            .Select(chat => new WrHistoryChat.ChatCandidate(chat.DemoId, null, chat.Text, chat.Index, chat.Tick,
                chat.FromUserId))
            .ToListAsync(cancellationToken);

        var demoIds = suspectedWrMessages.Select(x => x.DemoId)
            .Concat(suspectedIrcWrMessages.Select(x => x.DemoId))
            .Distinct()
            .ToList();

        var demoDates = await WrHistoryChat.LoadDemoDatesAsync(db, demoIds, cancellationToken);
        var demoUsers = await WrHistoryChat.LoadDemoUsersAsync(db, demoIds, cancellationToken);

        const string soldier = "Solly";
        const string demoman = "Demo";

        var output = new List<WrHistoryEntry>();
        foreach (var candidate in suspectedWrMessages)
        {
            var entry = WrHistoryChat.TryParseCandidateForMap(candidate, map, demoDates, demoUsers);
            if (entry != null)
            {
                output.Add(entry);
            }
        }

        foreach (var candidate in suspectedIrcWrMessages)
        {
            var entry = WrHistoryChat.TryParseCandidateForMap(candidate, map, demoDates, demoUsers);
            if (entry != null)
            {
                output.Add(entry);
            }
        }

        var classOutput = @class switch
        {
            "S" => output.Where(x => x.Class == soldier),
            "D" => output.Where(x => x.Class == demoman),
            _ => Enumerable.Empty<WrHistoryEntry>()
        };

        classOutput = classOutput.Where(x => string.Equals(x.RecordType, "WR", StringComparison.OrdinalIgnoreCase));

        var wrHistory = WrHistoryChat.BuildWrHistory(classOutput, includeAll: false)
            .ToList();

        var csvPath = WrHistoryCsv.Write(ArchivePath.TempRoot, map, @class, wrHistory, cancellationToken);

        foreach (var wrHistoryEntry in wrHistory)
        {
            var date = ArchiveUtils.FormatDate(wrHistoryEntry.Date);
            var steam = wrHistoryEntry.SteamId64?.ToString(CultureInfo.InvariantCulture)
                        ?? wrHistoryEntry.SteamId
                        ?? "unknown";
            var segment = WrHistoryChat.GetSegment(wrHistoryEntry);
            var evidence = WrHistoryChat.GetEvidenceKind(wrHistoryEntry);
            var run = string.IsNullOrWhiteSpace(wrHistoryEntry.RunTime) ? string.Empty : $" run {wrHistoryEntry.RunTime}";
            var split = string.IsNullOrWhiteSpace(wrHistoryEntry.Split) ? string.Empty : $" split {wrHistoryEntry.Split}";
            var improvement = string.IsNullOrWhiteSpace(wrHistoryEntry.Improvement)
                ? string.Empty
                : $" imp {wrHistoryEntry.Improvement}";
            Console.WriteLine(
                $"{date} - {wrHistoryEntry.RecordType} {segment} {evidence} - {wrHistoryEntry.RecordTime}{run}{split}{improvement} - {wrHistoryEntry.Player} ({wrHistoryEntry.Map}) ({wrHistoryEntry.DemoId}) [{steam}]");
        }

        Console.WriteLine($"CSV: {csvPath}");
    }

}
