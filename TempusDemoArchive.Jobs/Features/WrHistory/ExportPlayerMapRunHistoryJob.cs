using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TempusDemoArchive.Persistence.Models.STVs;

namespace TempusDemoArchive.Jobs;

public class ExportPlayerMapRunHistoryJob : IJob
{
    private static readonly Regex MapRunRegex = new(
        $@"^Tempus \| \((?<class>[^)]+)\) (?<player>.*?) map run (?<run>{TempusTime.TimePattern}) \((?:(?<label>WR|PR)\s*)?(?<split>{TempusTime.SignedTimePattern})\)(?: \| (?<improvement>{TempusTime.TimePattern}) improvement!?)?\!?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

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
            .Select(chat => new ChatCandidate(chat.DemoId, chat.Stv!.Header.Map, chat.Text))
            .ToListAsync(cancellationToken);

        if (candidateChats.Count == 0)
        {
            Console.WriteLine("No map run messages found for that map.");
            return;
        }

        var demoIds = candidateChats.Select(x => x.DemoId).Distinct().ToList();
        var demoDates = await LoadDemoDatesAsync(db, demoIds, cancellationToken);
        var demoUsers = await LoadDemoUsersAsync(db, demoIds, cancellationToken);

        var target = ResolveTargetIdentity(playerIdentifier);
        if (target == null)
        {
            Console.WriteLine("Unable to parse target identifier.");
            return;
        }

        var results = new List<MapRunEntry>();
        foreach (var candidate in candidateChats)
        {
            var match = MapRunRegex.Match(candidate.Text);
            if (!match.Success)
            {
                continue;
            }

            var detectedClass = NormalizeClass(match.Groups["class"].Value);
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
            var identity = ResolveUserIdentity(candidate, playerName, demoUsers);
            if (identity == null || !target.Matches(identity))
            {
                continue;
            }

            demoDates.TryGetValue(candidate.DemoId, out var date);
            results.Add(new MapRunEntry(
                date,
                candidate.DemoId,
                candidate.Map,
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
        var value = Environment.GetEnvironmentVariable("TEMPUS_MAPRUN_INCLUDE_WR");
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static TargetIdentity? ResolveTargetIdentity(string identifier)
    {
        if (long.TryParse(identifier, out var steam64))
        {
            return TargetIdentity.FromSteam64(steam64);
        }

        return TargetIdentity.FromSteamId(identifier);
    }

    private static bool IsClassMatch(string detectedClass, string input)
    {
        if (input == "S")
        {
            return string.Equals(detectedClass, "Solly", StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(detectedClass, "Demo", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeClass(string value)
    {
        if (string.Equals(value, "Soldier", StringComparison.OrdinalIgnoreCase))
        {
            return "Solly";
        }

        if (string.Equals(value, "Demoman", StringComparison.OrdinalIgnoreCase))
        {
            return "Demo";
        }

        return value;
    }

    private static async Task<Dictionary<ulong, DateTime?>> LoadDemoDatesAsync(ArchiveDbContext db,
        IReadOnlyList<ulong> demoIds, CancellationToken cancellationToken)
    {
        if (demoIds.Count == 0)
        {
            return new Dictionary<ulong, DateTime?>();
        }

        var demoDates = new List<(ulong Id, double Date)>();
        foreach (var chunk in DbChunk.Chunk(demoIds))
        {
            var chunkDates = await db.Demos
                .AsNoTracking()
                .Where(x => chunk.Contains(x.Id))
                .Select(x => new { x.Id, x.Date })
                .ToListAsync(cancellationToken);
            demoDates.AddRange(chunkDates.Select(x => (x.Id, x.Date)));
        }

        return demoDates.ToDictionary(x => x.Id, x => (DateTime?)ArchiveUtils.GetDateFromTimestamp(x.Date));
    }

    private static async Task<Dictionary<ulong, DemoUsers>> LoadDemoUsersAsync(ArchiveDbContext db,
        IReadOnlyList<ulong> demoIds, CancellationToken cancellationToken)
    {
        if (demoIds.Count == 0)
        {
            return new Dictionary<ulong, DemoUsers>();
        }

        var users = new List<StvUser>();
        foreach (var chunk in DbChunk.Chunk(demoIds))
        {
            var chunkUsers = await db.StvUsers
                .AsNoTracking()
                .Where(x => chunk.Contains(x.DemoId))
                .ToListAsync(cancellationToken);
            users.AddRange(chunkUsers);
        }

        return BuildDemoUsers(users);
    }

    private static Dictionary<ulong, DemoUsers> BuildDemoUsers(IEnumerable<StvUser> users)
    {
        var demoUsers = new Dictionary<ulong, DemoUsers>();
        foreach (var group in users.GroupBy(user => user.DemoId))
        {
            var byUserId = new Dictionary<int, UserIdentity>();
            var byName = new Dictionary<string, UserIdentity?>(StringComparer.OrdinalIgnoreCase);
            foreach (var user in group)
            {
                var identity = new UserIdentity(user.SteamId64, user.SteamIdClean ?? user.SteamId);
                if (user.UserId.HasValue && !byUserId.ContainsKey(user.UserId.Value))
                {
                    byUserId[user.UserId.Value] = identity;
                }

                var name = user.Name.Trim();
                if (name.Length == 0)
                {
                    continue;
                }

                if (byName.TryGetValue(name, out var existing))
                {
                    if (existing != null)
                    {
                        byName[name] = null;
                    }
                }
                else
                {
                    byName[name] = identity;
                }
            }

            demoUsers[group.Key] = new DemoUsers(byUserId, byName);
        }

        return demoUsers;
    }

    private static UserIdentity? ResolveUserIdentity(ChatCandidate candidate, string player,
        IReadOnlyDictionary<ulong, DemoUsers> demoUsers)
    {
        if (!demoUsers.TryGetValue(candidate.DemoId, out var users))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(player))
        {
            return null;
        }

        var normalized = player.Trim();
        if (users.ByName.TryGetValue(normalized, out var byName))
        {
            return byName;
        }

        if (normalized.EndsWith("...", StringComparison.Ordinal) && normalized.Length > 3)
        {
            var prefix = normalized.Substring(0, normalized.Length - 3).Trim();
            if (prefix.Length > 0)
            {
                var matches = users.ByName
                    .Where(entry => entry.Value != null
                                    && entry.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Select(entry => entry.Value)
                    .Distinct()
                    .ToList();

                if (matches.Count == 1)
                {
                    return matches[0];
                }
            }
        }

        return null;
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

    private sealed record ChatCandidate(ulong DemoId, string Map, string Text);
    private sealed record DemoUsers(Dictionary<int, UserIdentity> ByUserId, Dictionary<string, UserIdentity?> ByName);
    private sealed record MapRunEntry(DateTime? Date, ulong DemoId, string Map, string Class, string Label,
        string RunTime, string? Split, string? Improvement);

    private sealed class TargetIdentity
    {
        private readonly long? _steam64;
        private readonly string? _steamId;

        private TargetIdentity(long? steam64, string? steamId)
        {
            _steam64 = steam64;
            _steamId = steamId;
        }

        public static TargetIdentity FromSteam64(long steam64) => new(steam64, null);
        public static TargetIdentity FromSteamId(string steamId) => new(null, steamId);

        public bool Matches(UserIdentity identity)
        {
            if (_steam64.HasValue && identity.SteamId64.HasValue)
            {
                return _steam64.Value == identity.SteamId64.Value;
            }

            if (!string.IsNullOrWhiteSpace(_steamId) && !string.IsNullOrWhiteSpace(identity.SteamId))
            {
                return string.Equals(_steamId, identity.SteamId, StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }
    }
}
