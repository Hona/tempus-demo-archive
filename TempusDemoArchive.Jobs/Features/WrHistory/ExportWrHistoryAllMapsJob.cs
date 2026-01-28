using Microsoft.EntityFrameworkCore;

namespace TempusDemoArchive.Jobs;

public class ExportWrHistoryAllMapsJob : IJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var outputRoot = Path.Combine(ArchivePath.TempRoot, "wr-history-all");
        Directory.CreateDirectory(outputRoot);

        Console.WriteLine("WR history export uses fixed rules (no env toggles).");
        Console.WriteLine("- Includes subrecords (bonus/course/segments)");
        Console.WriteLine("- Emits per-segment WR state changes (improvements + wipes)");
        Console.WriteLine("- Includes record messages + reputable announcements for wiped detection");
        Console.WriteLine("- Suppresses duplicate non-record rows when a record message exists for the same time");
        Console.WriteLine($"Output dir: {outputRoot}");

        await using var db = new ArchiveDbContext();

        var entries = new List<WrHistoryEntry>();
        var demoBuffer = new List<(ulong DemoId, string Map)>(DbChunk.DefaultSize);
        var processedDemos = 0;
        var processedChunks = 0;

        await foreach (var stv in db.Stvs
                           .AsNoTracking()
                           .Select(x => new { x.DemoId, x.Header.Map })
                           .AsAsyncEnumerable()
                           .WithCancellation(cancellationToken))
        {
            demoBuffer.Add((stv.DemoId, stv.Map));
            if (demoBuffer.Count < DbChunk.DefaultSize)
            {
                continue;
            }

            await AddEntriesFromDemoChunkAsync(db, demoBuffer, entries, cancellationToken);
            processedDemos += demoBuffer.Count;
            processedChunks++;
            demoBuffer.Clear();

            if (processedChunks % 100 == 0)
            {
                Console.WriteLine($"Processed demos: {processedDemos:N0} | Parsed entries: {entries.Count:N0}");
            }
        }

        if (demoBuffer.Count > 0)
        {
            await AddEntriesFromDemoChunkAsync(db, demoBuffer, entries, cancellationToken);
            processedDemos += demoBuffer.Count;
            demoBuffer.Clear();
        }

        var filtered = entries
            .Where(entry => string.Equals(entry.RecordType, "WR", StringComparison.OrdinalIgnoreCase))
            .Where(entry => entry.Class == "Solly" || entry.Class == "Demo")
            .ToList();

        var grouped = filtered
            .GroupBy(entry => new { entry.Map, entry.Class })
            .OrderBy(group => group.Key.Map)
            .ThenBy(group => group.Key.Class)
            .ToList();

        var totalFiles = 0;
        foreach (var group in grouped)
        {
            var history = WrHistoryChat.BuildWrHistory(group, includeAll: false)
                .ToList();

            if (history.Count == 0)
            {
                continue;
            }

            var filePath = WrHistoryCsv.Write(outputRoot, group.Key.Map, group.Key.Class, history, cancellationToken);
            totalFiles++;
            Console.WriteLine($"Wrote {filePath}");
        }

        Console.WriteLine($"Maps: {grouped.Select(g => g.Key.Map).Distinct().Count():N0}");
        Console.WriteLine($"Files: {totalFiles:N0}");
    }

    private static async Task AddEntriesFromDemoChunkAsync(ArchiveDbContext db, List<(ulong DemoId, string Map)> demos,
        List<WrHistoryEntry> entries, CancellationToken cancellationToken)
    {
        if (demos.Count == 0)
        {
            return;
        }

        var demoIds = demos.Select(x => x.DemoId).ToList();
        var mapByDemoId = demos.ToDictionary(x => x.DemoId, x => x.Map);

        var tempusChats = await db.StvChats
            .AsNoTracking()
            .Where(chat => demoIds.Contains(chat.DemoId))
            .WhereLikelyTempusWrMessage()
            .Select(chat => new { chat.DemoId, chat.Text, chat.FromUserId, chat.Index, chat.Tick })
            .ToListAsync(cancellationToken);

        var ircChats = await db.StvChats
            .AsNoTracking()
            .Where(chat => demoIds.Contains(chat.DemoId))
            .WhereLikelyIrcWrMessage()
            .Select(chat => new { chat.DemoId, chat.Text, chat.FromUserId, chat.Index, chat.Tick })
            .ToListAsync(cancellationToken);

        if (tempusChats.Count == 0 && ircChats.Count == 0)
        {
            return;
        }

        var candidates = new List<WrHistoryChat.ChatCandidate>(tempusChats.Count + ircChats.Count);
        foreach (var chat in tempusChats)
        {
            if (mapByDemoId.TryGetValue(chat.DemoId, out var map))
            {
                candidates.Add(new WrHistoryChat.ChatCandidate(chat.DemoId, map, chat.Text, chat.Index, chat.Tick,
                    chat.FromUserId));
            }
        }

        foreach (var chat in ircChats)
        {
            candidates.Add(new WrHistoryChat.ChatCandidate(chat.DemoId, null, chat.Text, chat.Index, chat.Tick,
                chat.FromUserId));
        }

        var candidateDemoIds = candidates.Select(x => x.DemoId).Distinct().ToList();
        var demoDates = await WrHistoryChat.LoadDemoDatesAsync(db, candidateDemoIds, cancellationToken);
        var demoUsers = await WrHistoryChat.LoadDemoUsersAsync(db, candidateDemoIds, cancellationToken);

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = WrHistoryChat.TryParseCandidateAnyMap(candidate, demoDates, demoUsers);

            if (entry != null)
            {
                entries.Add(entry);
            }
        }
    }

}
