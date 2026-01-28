using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace TempusDemoArchive.Jobs;

public sealed class AuditWrHistoryFromChatJob : IJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var map = JobPrompts.ReadMapName();
        if (map == null)
        {
            return;
        }

        await using var db = new ArchiveDbContext();
        var mapPrefix = map + "_";

        var mapDemos = await db.Stvs
            .AsNoTracking()
            .Where(x => x.Header.Map == map || EF.Functions.Like(x.Header.Map, mapPrefix + "%"))
            .Select(x => new { x.DemoId, x.Header.Map })
            .ToListAsync(cancellationToken);

        if (mapDemos.Count == 0)
        {
            Console.WriteLine($"No demos found for map: {map}");
            return;
        }

        var mapByDemoId = mapDemos.ToDictionary(x => x.DemoId, x => x.Map);
        var mapDemoIds = mapDemos.Select(x => x.DemoId).ToList();

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

        var candidates = suspectedWrMessages
            .Concat(suspectedIrcWrMessages)
            .ToList();

        if (candidates.Count == 0)
        {
            Console.WriteLine("No WR-like chat messages found.");
            return;
        }

        var demoIds = candidates.Select(x => x.DemoId)
            .Distinct()
            .ToList();

        var demoDates = await WrHistoryChat.LoadDemoDatesAsync(db, demoIds, cancellationToken);
        var demoUsers = await WrHistoryChat.LoadDemoUsersAsync(db, demoIds, cancellationToken);

        var parsed = new List<(WrHistoryEntry Entry, WrHistoryChat.ChatCandidate Candidate)>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = WrHistoryChat.TryParseCandidateForMap(candidate, map, demoDates, demoUsers);
            if (entry != null)
            {
                parsed.Add((entry, candidate));
            }
        }

        if (parsed.Count == 0)
        {
            Console.WriteLine("No parseable WR history entries found.");
            return;
        }

        var filtered = parsed
            .Where(x => string.Equals(x.Entry.RecordType, WrHistoryConstants.RecordType.Wr, StringComparison.OrdinalIgnoreCase))
            .Where(x => x.Entry.Class == "Solly" || x.Entry.Class == "Demo")
            .Where(x => x.Entry.Date != null)
            .ToList();

        var byKey = filtered
            .Select(x =>
            {
                if (!TempusTime.TryParseTimeCentiseconds(x.Entry.RecordTime, out var cs))
                {
                    return (BucketedEntry?)null;
                }

                var segment = WrHistoryChat.GetSegment(x.Entry);
                var evidence = WrHistoryChat.GetEvidenceKind(x.Entry);
                var evidenceSource = WrHistoryChat.GetEvidenceSource(x.Entry);
                return new BucketedEntry(x.Entry, x.Candidate, cs, segment, evidence, evidenceSource);
            })
            .Where(x => x != null)
            .Select(x => x!.Value)
            .GroupBy(x => new { x.Entry.Map, x.Entry.Class, x.Segment })
            .ToList();

        var anomalies = new List<AuditRow>();
        foreach (var group in byKey)
        {
            var recordTimes = group
                .Where(x => string.Equals(x.Evidence, WrHistoryConstants.EvidenceKind.Record, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Centiseconds)
                .ToList();

            if (recordTimes.Count == 0)
            {
                continue;
            }

            var bestRecord = recordTimes.Min();
            foreach (var item in group)
            {
                if (string.Equals(item.Evidence, WrHistoryConstants.EvidenceKind.Record, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (item.Centiseconds < bestRecord)
                {
                    anomalies.Add(new AuditRow(
                        Map: item.Entry.Map,
                        Class: item.Entry.Class,
                        Segment: item.Segment,
                        Evidence: item.Evidence,
                        EvidenceSource: item.EvidenceSource,
                        RecordTime: item.Entry.RecordTime,
                        Date: ArchiveUtils.FormatDate(item.Entry.Date),
                        DemoId: item.Entry.DemoId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                        ChatIndex: item.Candidate.ChatIndex.ToString(CultureInfo.InvariantCulture),
                        Text: SanitizeCsvText(item.Candidate.Text)));
                }
            }
        }

        var outputDir = Path.Combine(ArchivePath.TempRoot, "wr-history-audit");
        Directory.CreateDirectory(outputDir);
        var outputPath = Path.Combine(outputDir, $"{map}.csv");

        WriteCsv(outputPath, anomalies);

        Console.WriteLine($"Parsed entries: {filtered.Count:N0}");
        Console.WriteLine($"Anomalies (non-record faster than best record): {anomalies.Count:N0}");
        Console.WriteLine($"CSV: {outputPath}");
    }

    private readonly record struct BucketedEntry(
        WrHistoryEntry Entry,
        WrHistoryChat.ChatCandidate Candidate,
        int Centiseconds,
        string Segment,
        string Evidence,
        string EvidenceSource);

    private readonly record struct AuditRow(
        string Map,
        string Class,
        string Segment,
        string Evidence,
        string EvidenceSource,
        string RecordTime,
        string Date,
        string DemoId,
        string ChatIndex,
        string Text);

    private static string SanitizeCsvText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var lastLine = value.Split('\n').LastOrDefault() ?? value;
        lastLine = lastLine.Replace("\r", string.Empty).Trim();
        if (lastLine.Length > 500)
        {
            lastLine = lastLine.Substring(0, 500);
        }

        return lastLine;
    }

    private static void WriteCsv(string path, IReadOnlyList<AuditRow> rows)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine("map,class,segment,evidence,evidence_source,record_time,date,demo_id,chat_index,text");
        foreach (var row in rows)
        {
            writer.WriteLine(string.Join(",",
                Escape(row.Map),
                Escape(row.Class),
                Escape(row.Segment),
                Escape(row.Evidence),
                Escape(row.EvidenceSource),
                Escape(row.RecordTime),
                Escape(row.Date),
                Escape(row.DemoId),
                Escape(row.ChatIndex),
                Escape(row.Text)));
        }
    }

    private static string Escape(string value)
    {
        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
        {
            return '"' + value.Replace("\"", "\"\"") + '"';
        }

        return value;
    }
}
