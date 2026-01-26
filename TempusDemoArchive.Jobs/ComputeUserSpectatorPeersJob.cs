using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TempusDemoArchive.Persistence.Models.STVs;

namespace TempusDemoArchive.Jobs;

public class ComputeUserSpectatorPeersJob : IJob
{
    private static readonly HashSet<string> PlayableClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "soldier",
        "demoman"
    };

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine("Enter steam ID or Steam64:");
        var playerIdentifier = Console.ReadLine()?.Trim();

        if (string.IsNullOrWhiteSpace(playerIdentifier))
        {
            Console.WriteLine("No identifier provided.");
            return;
        }

        await using var db = new ArchiveDbContext();
        var userQuery = ArchiveQueries.SteamUserQuery(db, playerIdentifier)
            .AsNoTracking()
            .Where(user => user.UserId != null);

        var targetUsers = await userQuery
            .Select(user => new TargetUserEntry(user.DemoId, user.UserId!.Value, user.Name))
            .ToListAsync(cancellationToken);

        if (targetUsers.Count == 0)
        {
            Console.WriteLine("No demos found for that user.");
            return;
        }

        var displayName = targetUsers
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key)
            .FirstOrDefault() ?? playerIdentifier;

        var targetDemoUserIds = new Dictionary<ulong, HashSet<int>>();
        foreach (var entry in targetUsers)
        {
            if (!targetDemoUserIds.TryGetValue(entry.DemoId, out var ids))
            {
                ids = new HashSet<int>();
                targetDemoUserIds[entry.DemoId] = ids;
            }

            ids.Add(entry.UserId);
        }

        var demoIds = targetDemoUserIds.Keys.ToList();
        var metaByDemo = await LoadMetaAsync(db, demoIds, cancellationToken);
        var usersByDemo = await LoadUsersAsync(db, demoIds, cancellationToken);
        var spawnsByDemo = await LoadSpawnsAsync(db, demoIds, cancellationToken);
        var deathsByDemo = await LoadDeathsAsync(db, demoIds, cancellationToken);
        var teamsByDemo = await LoadTeamChangesAsync(db, demoIds, cancellationToken);

        var peers = new Dictionary<UserKey, PeerTotals>();
        var processedDemos = 0;

        foreach (var demoId in demoIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!metaByDemo.TryGetValue(demoId, out var meta))
            {
                continue;
            }

            if (!meta.IntervalPerTick.HasValue || meta.IntervalPerTick.Value <= 0)
            {
                continue;
            }

            if (!targetDemoUserIds.TryGetValue(demoId, out var targetUserIds))
            {
                continue;
            }

            IReadOnlyList<SpawnEvent> demoSpawns = spawnsByDemo.TryGetValue(demoId, out var spawnList)
                ? spawnList
                : Array.Empty<SpawnEvent>();
            IReadOnlyList<DeathEvent> demoDeaths = deathsByDemo.TryGetValue(demoId, out var deathList)
                ? deathList
                : Array.Empty<DeathEvent>();
            IReadOnlyList<TeamChangeEvent> demoTeams = teamsByDemo.TryGetValue(demoId, out var teamList)
                ? teamList
                : Array.Empty<TeamChangeEvent>();

            var demoEndTick = GetDemoEndTick(meta, demoSpawns, demoDeaths, demoTeams);
            if (demoEndTick <= 0)
            {
                continue;
            }

            var targetAlive = MergeIntervals(targetUserIds.SelectMany(userId =>
                BuildAliveIntervals(userId, demoSpawns, demoDeaths, demoTeams, demoEndTick)));
            var targetSpectator = MergeIntervals(targetUserIds.SelectMany(userId =>
                BuildSpectatorIntervals(userId, demoTeams, demoSpawns, demoEndTick)));

            if (targetAlive.Count == 0 && targetSpectator.Count == 0)
            {
                continue;
            }

            IReadOnlyList<UserEntry> demoUsers = usersByDemo.TryGetValue(demoId, out var userList)
                ? userList
                : Array.Empty<UserEntry>();

            var spawnsByUser = demoSpawns.GroupBy(spawn => spawn.UserId)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<SpawnEvent>)group.ToList());
            var deathsByUser = demoDeaths.GroupBy(death => death.UserId)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<DeathEvent>)group.ToList());
            var teamsByUser = demoTeams.GroupBy(change => change.UserId)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<TeamChangeEvent>)group.ToList());

            var anyOverlap = false;
            foreach (var user in demoUsers)
            {
                if (!user.UserId.HasValue || targetUserIds.Contains(user.UserId.Value))
                {
                    continue;
                }

                var steamId = user.SteamIdClean ?? user.SteamId;
                if (string.IsNullOrWhiteSpace(steamId))
                {
                    continue;
                }

                var key = new UserKey(user.SteamId64, steamId);
                if (!peers.TryGetValue(key, out var totals))
                {
                    totals = new PeerTotals();
                    peers[key] = totals;
                }

                totals.TrackName(user.Name);

                IReadOnlyList<SpawnEvent> otherSpawns = spawnsByUser.TryGetValue(user.UserId.Value, out var userSpawns)
                    ? userSpawns
                    : Array.Empty<SpawnEvent>();
                IReadOnlyList<DeathEvent> otherDeaths = deathsByUser.TryGetValue(user.UserId.Value, out var userDeaths)
                    ? userDeaths
                    : Array.Empty<DeathEvent>();
                IReadOnlyList<TeamChangeEvent> otherTeams = teamsByUser.TryGetValue(user.UserId.Value, out var userTeams)
                    ? userTeams
                    : Array.Empty<TeamChangeEvent>();

                var otherAlive = BuildAliveIntervals(user.UserId.Value, otherSpawns, otherDeaths, otherTeams, demoEndTick);
                var otherSpectator = BuildSpectatorIntervals(user.UserId.Value, otherTeams, otherSpawns, demoEndTick);

                var specsYouTicks = otherSpectator.Count == 0 || targetAlive.Count == 0
                    ? 0
                    : OverlapTicks(otherSpectator, targetAlive);
                var youSpecTicks = targetSpectator.Count == 0 || otherAlive.Count == 0
                    ? 0
                    : OverlapTicks(targetSpectator, otherAlive);

                if (specsYouTicks > 0)
                {
                    totals.SpecsYouSeconds += specsYouTicks * meta.IntervalPerTick.Value;
                    totals.SpecsYouDemoCount += 1;
                    anyOverlap = true;
                }

                if (youSpecTicks > 0)
                {
                    totals.YouSpecSeconds += youSpecTicks * meta.IntervalPerTick.Value;
                    totals.YouSpecDemoCount += 1;
                    anyOverlap = true;
                }
            }

            if (anyOverlap)
            {
                processedDemos += 1;
            }
        }

        if (peers.Count == 0)
        {
            Console.WriteLine("No spectator overlap found for that user.");
            return;
        }

        var rows = peers
            .Select(entry => new PeerRow(
                entry.Key.SteamId64,
                entry.Key.SteamId,
                entry.Value.DisplayName,
                entry.Value.SpecsYouSeconds,
                entry.Value.YouSpecSeconds,
                entry.Value.SpecsYouDemoCount,
                entry.Value.YouSpecDemoCount))
            .Where(row => row.SpecsYouSeconds > 0 || row.YouSpecSeconds > 0)
            .ToList();

        var fileName = ArchiveUtils.ToValidFileName($"spectator_peers_{playerIdentifier}.csv");
        var filePath = Path.Combine(ArchivePath.TempRoot, fileName);
        await WriteCsvAsync(filePath, rows, cancellationToken);

        Console.WriteLine($"Player: {displayName}");
        Console.WriteLine($"Demos processed: {processedDemos:N0}");
        Console.WriteLine("Spectate target is not logged; overlap is used as a proxy.");
        Console.WriteLine($"CSV: {filePath}");
        Console.WriteLine();

        Console.WriteLine("Top spectators while you were alive:");
        foreach (var row in rows.OrderByDescending(row => row.SpecsYouSeconds).Take(20))
        {
            Console.WriteLine($"{row.Name} | {FormatHours(row.SpecsYouSeconds)} | demos {row.SpecsYouDemoCount} | {FormatSteam(row)}");
        }

        Console.WriteLine();
        Console.WriteLine("Top players alive while you were spectating:");
        foreach (var row in rows.OrderByDescending(row => row.YouSpecSeconds).Take(20))
        {
            Console.WriteLine($"{row.Name} | {FormatHours(row.YouSpecSeconds)} | demos {row.YouSpecDemoCount} | {FormatSteam(row)}");
        }
    }

    private static async Task<Dictionary<ulong, DemoMeta>> LoadMetaAsync(ArchiveDbContext db,
        IReadOnlyList<ulong> demoIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<ulong, DemoMeta>();
        foreach (var chunk in ChunkIds(demoIds))
        {
            var metas = await db.Stvs
                .AsNoTracking()
                .Where(stv => chunk.Contains(stv.DemoId))
                .Select(stv => new DemoMeta(stv.DemoId, stv.Header.Map, stv.IntervalPerTick, stv.Header.Ticks))
                .ToListAsync(cancellationToken);
            foreach (var meta in metas)
            {
                result[meta.DemoId] = meta;
            }
        }

        return result;
    }

    private static async Task<Dictionary<ulong, List<UserEntry>>> LoadUsersAsync(ArchiveDbContext db,
        IReadOnlyList<ulong> demoIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<ulong, List<UserEntry>>();
        foreach (var chunk in ChunkIds(demoIds))
        {
            var users = await db.StvUsers
                .AsNoTracking()
                .Where(user => chunk.Contains(user.DemoId))
                .Select(user => new UserEntry(user.DemoId, user.UserId, user.Name, user.SteamIdClean,
                    user.SteamId, user.SteamId64))
                .ToListAsync(cancellationToken);
            foreach (var user in users)
            {
                if (!result.TryGetValue(user.DemoId, out var list))
                {
                    list = new List<UserEntry>();
                    result[user.DemoId] = list;
                }

                list.Add(user);
            }
        }

        return result;
    }

    private static async Task<Dictionary<ulong, List<SpawnEvent>>> LoadSpawnsAsync(ArchiveDbContext db,
        IReadOnlyList<ulong> demoIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<ulong, List<SpawnEvent>>();
        foreach (var chunk in ChunkIds(demoIds))
        {
            var spawns = await db.StvSpawns
                .AsNoTracking()
                .Where(spawn => chunk.Contains(spawn.DemoId))
                .Select(spawn => new SpawnEvent(spawn.DemoId, spawn.UserId, spawn.Tick, spawn.Class))
                .ToListAsync(cancellationToken);
            foreach (var spawn in spawns)
            {
                if (!result.TryGetValue(spawn.DemoId, out var list))
                {
                    list = new List<SpawnEvent>();
                    result[spawn.DemoId] = list;
                }

                list.Add(spawn);
            }
        }

        return result;
    }

    private static async Task<Dictionary<ulong, List<DeathEvent>>> LoadDeathsAsync(ArchiveDbContext db,
        IReadOnlyList<ulong> demoIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<ulong, List<DeathEvent>>();
        foreach (var chunk in ChunkIds(demoIds))
        {
            var deaths = await db.StvDeaths
                .AsNoTracking()
                .Where(death => chunk.Contains(death.DemoId))
                .Select(death => new DeathEvent(death.DemoId, death.VictimUserId, death.Tick))
                .ToListAsync(cancellationToken);
            foreach (var death in deaths)
            {
                if (!result.TryGetValue(death.DemoId, out var list))
                {
                    list = new List<DeathEvent>();
                    result[death.DemoId] = list;
                }

                list.Add(death);
            }
        }

        return result;
    }

    private static async Task<Dictionary<ulong, List<TeamChangeEvent>>> LoadTeamChangesAsync(ArchiveDbContext db,
        IReadOnlyList<ulong> demoIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<ulong, List<TeamChangeEvent>>();
        foreach (var chunk in ChunkIds(demoIds))
        {
            var changes = await db.StvTeamChanges
                .AsNoTracking()
                .Where(change => chunk.Contains(change.DemoId))
                .Select(change => new TeamChangeEvent(change.DemoId, change.UserId, change.Tick, change.Team,
                    change.Disconnect))
                .ToListAsync(cancellationToken);
            foreach (var change in changes)
            {
                if (!result.TryGetValue(change.DemoId, out var list))
                {
                    list = new List<TeamChangeEvent>();
                    result[change.DemoId] = list;
                }

                list.Add(change);
            }
        }

        return result;
    }

    private static IEnumerable<List<ulong>> ChunkIds(IReadOnlyList<ulong> demoIds, int size = 900)
    {
        for (var i = 0; i < demoIds.Count; i += size)
        {
            var chunk = demoIds.Skip(i).Take(size).ToList();
            if (chunk.Count == 0)
            {
                yield break;
            }

            yield return chunk;
        }
    }

    private static int GetDemoEndTick(DemoMeta meta, IReadOnlyList<SpawnEvent> spawns,
        IReadOnlyList<DeathEvent> deaths, IReadOnlyList<TeamChangeEvent> teamChanges)
    {
        var demoEndTick = meta.HeaderTicks ?? 0;
        if (demoEndTick > 0)
        {
            return demoEndTick;
        }

        var max = 0;
        if (spawns.Count > 0)
        {
            max = Math.Max(max, spawns.Max(entry => entry.Tick));
        }

        if (deaths.Count > 0)
        {
            max = Math.Max(max, deaths.Max(entry => entry.Tick));
        }

        if (teamChanges.Count > 0)
        {
            max = Math.Max(max, teamChanges.Max(entry => entry.Tick));
        }

        return max;
    }

    private static List<Interval> BuildAliveIntervals(int userId, IReadOnlyList<SpawnEvent> spawns,
        IReadOnlyList<DeathEvent> deaths, IReadOnlyList<TeamChangeEvent> teamChanges, int demoEndTick)
    {
        var events = new List<PlayerEvent>();
        foreach (var spawn in spawns)
        {
            if (spawn.UserId == userId)
            {
                events.Add(new PlayerEvent(spawn.Tick, EventKind.Spawn, spawn.Class));
            }
        }

        foreach (var death in deaths)
        {
            if (death.UserId == userId)
            {
                events.Add(new PlayerEvent(death.Tick, EventKind.Death, null));
            }
        }

        foreach (var change in teamChanges)
        {
            if (change.UserId == userId && IsSpectatorTeam(change.Team))
            {
                events.Add(new PlayerEvent(change.Tick, EventKind.Spectator, null));
            }

            if (change.UserId == userId && change.Disconnect)
            {
                events.Add(new PlayerEvent(change.Tick, EventKind.Spectator, null));
            }
        }

        events.Sort((left, right) =>
        {
            var tickCompare = left.Tick.CompareTo(right.Tick);
            if (tickCompare != 0)
            {
                return tickCompare;
            }

            return left.Kind.CompareTo(right.Kind);
        });

        var intervals = new List<Interval>();
        var alive = false;
        var currentClass = string.Empty;
        var startTick = 0;

        foreach (var entry in events)
        {
            if (entry.Kind == EventKind.Spawn)
            {
                if (alive)
                {
                    AddInterval(intervals, startTick, entry.Tick);
                }

                var spawnClass = entry.Class ?? string.Empty;
                if (IsPlayableClass(spawnClass))
                {
                    alive = true;
                    currentClass = spawnClass;
                    startTick = entry.Tick;
                }
                else
                {
                    alive = false;
                }

                continue;
            }

            if (!alive)
            {
                continue;
            }

            AddInterval(intervals, startTick, entry.Tick);
            alive = false;
        }

        if (alive)
        {
            AddInterval(intervals, startTick, demoEndTick);
        }

        return intervals;
    }

    private static List<Interval> BuildSpectatorIntervals(int userId, IReadOnlyList<TeamChangeEvent> teamChanges,
        IReadOnlyList<SpawnEvent> spawns, int demoEndTick)
    {
        var events = new List<SpectatorEvent>();
        foreach (var change in teamChanges)
        {
            if (change.UserId != userId)
            {
                continue;
            }

            if (change.Disconnect || !IsSpectatorTeam(change.Team))
            {
                events.Add(new SpectatorEvent(change.Tick, SpectatorEventKind.End));
                continue;
            }

            events.Add(new SpectatorEvent(change.Tick, SpectatorEventKind.Start));
        }

        foreach (var spawn in spawns)
        {
            if (spawn.UserId == userId)
            {
                events.Add(new SpectatorEvent(spawn.Tick, SpectatorEventKind.End));
            }
        }

        events.Sort((left, right) =>
        {
            var tickCompare = left.Tick.CompareTo(right.Tick);
            if (tickCompare != 0)
            {
                return tickCompare;
            }

            return left.Kind.CompareTo(right.Kind);
        });

        var intervals = new List<Interval>();
        var spectating = false;
        var startTick = 0;
        foreach (var entry in events)
        {
            if (entry.Kind == SpectatorEventKind.Start)
            {
                if (!spectating)
                {
                    spectating = true;
                    startTick = entry.Tick;
                }

                continue;
            }

            if (!spectating)
            {
                continue;
            }

            AddInterval(intervals, startTick, entry.Tick);
            spectating = false;
        }

        if (spectating)
        {
            AddInterval(intervals, startTick, demoEndTick);
        }

        return intervals;
    }

    private static List<Interval> MergeIntervals(IEnumerable<Interval> intervals)
    {
        var ordered = intervals
            .Where(interval => interval.EndTick > interval.StartTick)
            .OrderBy(interval => interval.StartTick)
            .ToList();

        if (ordered.Count <= 1)
        {
            return ordered;
        }

        var merged = new List<Interval>();
        var current = ordered[0];
        for (var i = 1; i < ordered.Count; i++)
        {
            var next = ordered[i];
            if (next.StartTick <= current.EndTick)
            {
                current = new Interval(current.StartTick, Math.Max(current.EndTick, next.EndTick));
                continue;
            }

            merged.Add(current);
            current = next;
        }

        merged.Add(current);
        return merged;
    }

    private static int OverlapTicks(IReadOnlyList<Interval> left, IReadOnlyList<Interval> right)
    {
        var total = 0;
        var i = 0;
        var j = 0;
        while (i < left.Count && j < right.Count)
        {
            var current = left[i];
            var other = right[j];
            var start = Math.Max(current.StartTick, other.StartTick);
            var end = Math.Min(current.EndTick, other.EndTick);
            if (end > start)
            {
                total += end - start;
            }

            if (current.EndTick <= other.EndTick)
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        return total;
    }

    private static void AddInterval(List<Interval> intervals, int startTick, int endTick)
    {
        if (endTick > startTick)
        {
            intervals.Add(new Interval(startTick, endTick));
        }
    }

    private static bool IsPlayableClass(string className)
    {
        return !string.IsNullOrWhiteSpace(className) && PlayableClasses.Contains(className);
    }

    private static bool IsSpectatorTeam(string team)
    {
        return string.Equals(team, "spectator", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WriteCsvAsync(string path, IReadOnlyList<PeerRow> rows,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>
        {
            "steam_id64,steam_id,name,specs_you_seconds,you_spec_seconds,specs_you_demos,you_spec_demos"
        };
        foreach (var row in rows)
        {
            var steam64 = row.SteamId64?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            var name = row.Name.Replace(",", " ");
            var steamId = row.SteamId.Replace(",", " ");
            lines.Add(string.Join(',', new[]
            {
                steam64,
                steamId,
                name,
                row.SpecsYouSeconds.ToString("0.##", CultureInfo.InvariantCulture),
                row.YouSpecSeconds.ToString("0.##", CultureInfo.InvariantCulture),
                row.SpecsYouDemoCount.ToString(CultureInfo.InvariantCulture),
                row.YouSpecDemoCount.ToString(CultureInfo.InvariantCulture)
            }));
        }

        await File.WriteAllLinesAsync(path, lines, cancellationToken);
    }

    private static string FormatHours(double seconds)
    {
        return (seconds / 3600d).ToString("0.##", CultureInfo.InvariantCulture) + "h";
    }

    private static string FormatSteam(PeerRow row)
    {
        return row.SteamId64?.ToString() ?? row.SteamId;
    }

    private sealed record TargetUserEntry(ulong DemoId, int UserId, string Name);
    private sealed record DemoMeta(ulong DemoId, string Map, double? IntervalPerTick, int? HeaderTicks);
    private sealed record UserEntry(ulong DemoId, int? UserId, string Name, string? SteamIdClean, string SteamId,
        long? SteamId64);
    private sealed record SpawnEvent(ulong DemoId, int UserId, int Tick, string Class);
    private sealed record DeathEvent(ulong DemoId, int UserId, int Tick);
    private sealed record TeamChangeEvent(ulong DemoId, int UserId, int Tick, string Team, bool Disconnect);
    private sealed record PlayerEvent(int Tick, EventKind Kind, string? Class);
    private sealed record SpectatorEvent(int Tick, SpectatorEventKind Kind);
    private sealed record Interval(int StartTick, int EndTick);
    private sealed record UserKey(long? SteamId64, string SteamId);
    private sealed record PeerRow(long? SteamId64, string SteamId, string Name, double SpecsYouSeconds,
        double YouSpecSeconds, int SpecsYouDemoCount, int YouSpecDemoCount);

    private sealed class PeerTotals
    {
        public double SpecsYouSeconds { get; set; }
        public double YouSpecSeconds { get; set; }
        public int SpecsYouDemoCount { get; set; }
        public int YouSpecDemoCount { get; set; }
        private readonly Dictionary<string, int> _nameCounts = new(StringComparer.OrdinalIgnoreCase);

        public void TrackName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            if (_nameCounts.TryGetValue(name, out var current))
            {
                _nameCounts[name] = current + 1;
                return;
            }

            _nameCounts[name] = 1;
        }

        public string DisplayName => _nameCounts
            .OrderByDescending(entry => entry.Value)
            .Select(entry => entry.Key)
            .FirstOrDefault() ?? "unknown";
    }

    private enum EventKind
    {
        Death = 0,
        Spectator = 1,
        Spawn = 2
    }

    private enum SpectatorEventKind
    {
        Start = 0,
        End = 1
    }
}
