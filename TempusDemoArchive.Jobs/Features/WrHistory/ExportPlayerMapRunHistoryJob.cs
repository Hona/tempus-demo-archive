using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace TempusDemoArchive.Jobs;

public class ExportPlayerMapRunHistoryJob : IJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine("Enter steam ID or Steam64:");
        var playerIdentifier = Console.ReadLine()?.Trim();

        if (string.IsNullOrWhiteSpace(playerIdentifier))
        {
            Console.WriteLine("No identifier provided.");
            return;
        }

        Console.WriteLine("Input map name:");
        var map = Console.ReadLine()?.Trim();

        if (string.IsNullOrWhiteSpace(map))
        {
            Console.WriteLine("No map name provided.");
            return;
        }

        Console.WriteLine("Input class 'D' or 'S':");
        var @class = Console.ReadLine()!.ToUpper().Trim();
        if (@class != "D" && @class != "S")
        {
            throw new InvalidOperationException("Invalid class");
        }

        var includeWr = GetIncludeWr();
        Console.WriteLine($"Include WR map runs: {includeWr}");

        await using var db = new ArchiveDbContext();

        var mapPrefix = map + "_";
        var candidateChats = await db.StvChats
            .AsNoTracking()
            .Where(chat => chat.Stv != null)
            .Where(chat => chat.Stv!.Header.Map == map
                           || EF.Functions.Like(chat.Stv.Header.Map, mapPrefix + "%"))
            .Where(chat => EF.Functions.Like(chat.Text, "Tempus | (%"))
            .Where(chat => chat.Text.Contains(" map run "))
            .Select(chat => new ExtractWrHistoryFromChatJob.ChatCandidate(chat.DemoId, chat.Stv!.Header.Map, chat.Text,
                chat.FromUserId))
            .ToListAsync(cancellationToken);

        if (candidateChats.Count == 0)
        {
            Console.WriteLine("No map run messages found for that map.");
            return;
        }

        var demoIds = candidateChats.Select(x => x.DemoId).Distinct().ToList();
        var demoDates = await ExtractWrHistoryFromChatJob.LoadDemoDatesAsync(db, demoIds, cancellationToken);
        var demoUsers = await ExtractWrHistoryFromChatJob.LoadDemoUsersAsync(db, demoIds, cancellationToken);

        var results = new List<MapRunEntry>();
        foreach (var candidate in candidateChats)
        {
            var match = WrChatRegexes.MapRun.Match(candidate.Text);
            if (!match.Success)
            {
                continue;
            }

            var detectedClass = WrChatNormalizer.NormalizeClass(match.Groups["class"].Value);
            if (!IsClassMatch(detectedClass, @class))
            {
                continue;
            }

            var label = match.Groups["label"].Value;
            if (!string.Equals(label, "PR", StringComparison.OrdinalIgnoreCase)
                && !(includeWr && string.Equals(label, "WR", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var playerName = match.Groups["player"].Value.Trim();
            var resolved = ExtractWrHistoryFromChatJob.ResolveUserIdentity(candidate, playerName, demoUsers);
            if (resolved.Identity == null || !MatchesTarget(playerIdentifier, resolved.Identity))
            {
                continue;
            }

            demoDates.TryGetValue(candidate.DemoId, out var date);
            results.Add(new MapRunEntry(
                date,
                candidate.DemoId,
                candidate.Map ?? map,
                detectedClass,
                label.ToUpperInvariant(),
                TempusTime.NormalizeTime(match.Groups["run"].Value),
                TempusTime.NormalizeSignedTime(match.Groups["split"].Value),
                match.Groups["improvement"].Success ? TempusTime.NormalizeTime(match.Groups["improvement"].Value) : null));
        }

        if (results.Count == 0)
        {
            Console.WriteLine("No matching map run history found for that player.");
            return;
        }

        var ordered = results.OrderBy(entry => entry.Date).ThenBy(entry => entry.DemoId).ToList();
        var filePath = WriteCsv(playerIdentifier, map, @class, ordered);

        Console.WriteLine($"Found: {ordered.Count:N0} entries");
        Console.WriteLine($"CSV: {filePath}");
        foreach (var entry in ordered.Take(20))
        {
            var date = ArchiveUtils.FormatDate(entry.Date);
            var improvement = entry.Improvement == null ? string.Empty : $" imp {entry.Improvement}";
            Console.WriteLine($"{date} - {entry.Label} - {entry.RunTime} split {entry.Split}{improvement}");
        }
    }

    private static bool GetIncludeWr()
    {
        return EnvVar.GetBool("TEMPUS_MAPRUN_INCLUDE_WR");
    }

    private static bool MatchesTarget(string identifier, UserIdentity identity)
    {
        if (identity.SteamId64.HasValue && long.TryParse(identifier, out var steam64))
        {
            return identity.SteamId64.Value == steam64;
        }

        if (!string.IsNullOrWhiteSpace(identity.SteamId))
        {
            return string.Equals(identity.SteamId, identifier, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool IsClassMatch(string detectedClass, string input)
    {
        if (input == "S")
        {
            return string.Equals(detectedClass, "Solly", StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(detectedClass, "Demo", StringComparison.OrdinalIgnoreCase);
    }

    private static string WriteCsv(string identifier, string map, string @class,
        IReadOnlyList<MapRunEntry> entries)
    {
        var fileName = ArchiveUtils.ToValidFileName($"map_run_history_{identifier}_{map}_{@class}.csv");
        var filePath = Path.Combine(ArchivePath.TempRoot, fileName);
        var lines = new List<string>
        {
            "date,map,class,label,run_time,split,improvement,demo_id"
        };

        foreach (var entry in entries)
        {
            var date = ArchiveUtils.FormatDate(entry.Date);
            lines.Add(string.Join(',', new[]
            {
                date,
                entry.Map.Replace(',', ' '),
                entry.Class,
                entry.Label,
                entry.RunTime,
                entry.Split ?? string.Empty,
                entry.Improvement ?? string.Empty,
                entry.DemoId.ToString()
            }));
        }

        File.WriteAllLines(filePath, lines);
        return filePath;
    }

    private sealed record MapRunEntry(DateTime? Date, ulong DemoId, string Map, string Class, string Label,
        string RunTime, string? Split, string? Improvement);
}
