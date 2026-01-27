using Microsoft.EntityFrameworkCore;

namespace TempusDemoArchive.Jobs;

public class ExportWrHistoryAllMapsJob : IJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var includeSubRecords = GetIncludeSubRecords();
        var includeInferred = GetIncludeInferred();
        var includeLookup = GetIncludeLookup();
        var includeAll = GetIncludeAllEntries();
        var outputRoot = Path.Combine(ArchivePath.TempRoot, "wr-history-all");
        Directory.CreateDirectory(outputRoot);

        Console.WriteLine($"Include subrecords: {includeSubRecords}");
        Console.WriteLine($"Include inferred: {includeInferred}");
        Console.WriteLine($"Include lookup: {includeLookup}");
        Console.WriteLine($"Include all entries: {includeAll}");
        Console.WriteLine($"Output dir: {outputRoot}");

        await using var db = new ArchiveDbContext();

        var mapDemos = await db.Stvs
            .AsNoTracking()
            .Select(x => new { x.DemoId, x.Header.Map })
            .ToListAsync(cancellationToken);
        var mapByDemoId = mapDemos.ToDictionary(x => x.DemoId, x => x.Map);

        var tempusChats = await db.StvChats
            .AsNoTracking()
            .Where(chat => EF.Functions.Like(chat.Text, "Tempus | (%"))
            .Where(chat => (EF.Functions.Like(chat.Text, "%map run%")
                            && EF.Functions.Like(chat.Text, "%WR%"))
                           || EF.Functions.Like(chat.Text, "%beat the map record%")
                           || EF.Functions.Like(chat.Text, "%set the first map record%")
                           || EF.Functions.Like(chat.Text, "%is ranked%with time%")
                           || EF.Functions.Like(chat.Text, "%set Bonus%")
                           || EF.Functions.Like(chat.Text, "%set Course%")
                           || EF.Functions.Like(chat.Text, "%set C%")
                           || EF.Functions.Like(chat.Text, "%broke%Bonus%")
                           || EF.Functions.Like(chat.Text, "%broke%Course%")
                           || EF.Functions.Like(chat.Text, "%broke C%")
                           || EF.Functions.Like(chat.Text, "% WR)%"))
            .Select(chat => new { chat.DemoId, chat.Text, chat.FromUserId })
            .ToListAsync(cancellationToken);

        var ircChats = await db.StvChats
            .AsNoTracking()
            .Where(chat => EF.Functions.Like(chat.Text, ":: Tempus -%")
                           || EF.Functions.Like(chat.Text, ":: (%"))
            .Where(chat => chat.Text.Contains(" WR: "))
            .Where(chat => chat.Text.Contains(" broke ") || chat.Text.Contains(" set "))
            .Select(chat => new { chat.DemoId, chat.Text, chat.FromUserId })
            .ToListAsync(cancellationToken);

        var candidates = new List<ExtractWrHistoryFromChatJob.ChatCandidate>(tempusChats.Count + ircChats.Count);
        foreach (var chat in tempusChats)
        {
            if (mapByDemoId.TryGetValue(chat.DemoId, out var map))
            {
                candidates.Add(new ExtractWrHistoryFromChatJob.ChatCandidate(chat.DemoId, map, chat.Text,
                    chat.FromUserId));
            }
        }

        foreach (var chat in ircChats)
        {
            candidates.Add(new ExtractWrHistoryFromChatJob.ChatCandidate(chat.DemoId, null, chat.Text, chat.FromUserId));
        }

        var demoIds = candidates.Select(x => x.DemoId).Distinct().ToList();
        var demoDates = await ExtractWrHistoryFromChatJob.LoadDemoDatesAsync(db, demoIds, cancellationToken);
        var demoUsers = await ExtractWrHistoryFromChatJob.LoadDemoUsersAsync(db, demoIds, cancellationToken);

        var entries = new List<WrHistoryEntry>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            WrHistoryEntry? entry = null;
            if (!string.IsNullOrWhiteSpace(candidate.Map))
            {
                entry = ExtractWrHistoryFromChatJob.TryParseTempusRecord(candidate, candidate.Map!, demoDates, demoUsers);
            }
            else
            {
                entry = ExtractWrHistoryFromChatJob.TryParseIrcRecord(candidate, null, demoDates, demoUsers);
            }

            if (entry != null)
            {
                entries.Add(entry);
            }
        }

        var filtered = entries
            .Where(entry => string.Equals(entry.RecordType, "WR", StringComparison.OrdinalIgnoreCase))
            .Where(entry => entry.Class == "Solly" || entry.Class == "Demo")
            .ToList();

        if (!includeSubRecords)
        {
            filtered = filtered
                .Where(entry => !ExtractWrHistoryFromChatJob.IsSubRecordSource(entry.Source))
                .ToList();
        }

        if (!includeInferred)
        {
            filtered = filtered
                .Where(entry => !entry.Inferred)
                .ToList();
        }

        if (!includeLookup)
        {
            filtered = filtered
                .Where(entry => !entry.IsLookup)
                .ToList();
        }

        var grouped = filtered
            .GroupBy(entry => new { entry.Map, entry.Class })
            .OrderBy(group => group.Key.Map)
            .ThenBy(group => group.Key.Class)
            .ToList();

        var totalFiles = 0;
        foreach (var group in grouped)
        {
            var history = ExtractWrHistoryFromChatJob.BuildWrHistory(group, includeAll)
                .OrderBy(entry => entry.Date)
                .ThenBy(entry => entry.RecordTime)
                .ToList();

            if (history.Count == 0)
            {
                continue;
            }

            var filePath = WriteCsv(outputRoot, group.Key.Map, group.Key.Class, history);
            totalFiles++;
            Console.WriteLine($"Wrote {filePath}");
        }

        Console.WriteLine($"Maps: {grouped.Select(g => g.Key.Map).Distinct().Count():N0}");
        Console.WriteLine($"Files: {totalFiles:N0}");
    }

    private static string WriteCsv(string outputRoot, string map, string @class, IReadOnlyList<WrHistoryEntry> entries)
    {
        var fileName = ArchiveUtils.ToValidFileName($"wr_history_{map}_{@class}.csv");
        var filePath = Path.Combine(outputRoot, fileName);
        var lines = new List<string>
        {
            "date,record_time,player,map,record_type,source,run_time,split,improvement,inferred,demo_id,steam_id64,steam_id,steam_candidates"
        };

        foreach (var entry in entries)
        {
            var date = ArchiveUtils.FormatDate(entry.Date);
            lines.Add(string.Join(',', new[]
            {
                date,
                entry.RecordTime,
                entry.Player.Replace(',', ' '),
                entry.Map.Replace(',', ' '),
                entry.RecordType,
                entry.Source.Replace(',', ' '),
                entry.RunTime ?? string.Empty,
                entry.Split ?? string.Empty,
                entry.Improvement ?? string.Empty,
                entry.Inferred ? "true" : "false",
                entry.DemoId?.ToString() ?? string.Empty,
                entry.SteamId64?.ToString() ?? string.Empty,
                entry.SteamId ?? string.Empty,
                entry.SteamCandidates ?? string.Empty
            }));
        }

        File.WriteAllLines(filePath, lines);
        return filePath;
    }

    private static bool GetIncludeSubRecords()
    {
        return EnvVar.GetBool("TEMPUS_WR_INCLUDE_SUBRECORDS");
    }

    private static bool GetIncludeAllEntries()
    {
        return EnvVar.GetBool("TEMPUS_WR_INCLUDE_ALL");
    }

    private static bool GetIncludeInferred()
    {
        return EnvVar.GetBool("TEMPUS_WR_INCLUDE_INFERRED");
    }

    private static bool GetIncludeLookup()
    {
        return EnvVar.GetBool("TEMPUS_WR_INCLUDE_LOOKUP");
    }
}
