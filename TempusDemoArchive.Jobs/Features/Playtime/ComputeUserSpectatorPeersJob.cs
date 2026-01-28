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
        var playerIdentifier = JobPrompts.ReadSteamIdentifier();
        if (playerIdentifier == null)
        {
            return;
        }

        await using var db = new ArchiveDbContext();

        var resolvedUser = await PlaytimeUserResolver.ResolveAsync(db, playerIdentifier, cancellationToken);
        if (resolvedUser == null)
        {
            Console.WriteLine("No demos found for that user.");
            return;
        }

        var displayName = resolvedUser.DisplayName;
        var targetDemoUserIds = resolvedUser.DemoUserIds;
        var demoIds = resolvedUser.DemoIds;
        var metaByDemo = await PlaytimeMetaLoader.LoadByDemoIdAsync(db, demoIds, cancellationToken);
        var usersByDemo = await PlaytimeEventLoader.LoadUsersAsync(db, demoIds, cancellationToken);
        var spawnsByDemo = await PlaytimeEventLoader.LoadSpawnsAsync(db, demoIds, cancellationToken);
        var deathsByDemo = await PlaytimeEventLoader.LoadDeathsAsync(db, demoIds, cancellationToken);
        var teamsByDemo = await PlaytimeEventLoader.LoadTeamChangesAsync(db, demoIds, cancellationToken);

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

            IReadOnlyList<PlaytimeSpawnEvent> demoSpawns = spawnsByDemo.TryGetValue(demoId, out var spawnList)
                ? spawnList
                : Array.Empty<PlaytimeSpawnEvent>();
            IReadOnlyList<PlaytimeDeathEvent> demoDeaths = deathsByDemo.TryGetValue(demoId, out var deathList)
                ? deathList
                : Array.Empty<PlaytimeDeathEvent>();
            IReadOnlyList<PlaytimeTeamChangeEvent> demoTeams = teamsByDemo.TryGetValue(demoId, out var teamList)
                ? teamList
                : Array.Empty<PlaytimeTeamChangeEvent>();

            var demoEndTick = PlaytimeCalculator.GetDemoEndTick(meta, demoSpawns, demoDeaths, demoTeams);
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

            IReadOnlyList<PlaytimeUserEntry> demoUsers = usersByDemo.TryGetValue(demoId, out var userList)
                ? userList
                : Array.Empty<PlaytimeUserEntry>();

            var spawnsByUser = demoSpawns.GroupBy(spawn => spawn.UserId)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<PlaytimeSpawnEvent>)group.ToList());
            var deathsByUser = demoDeaths.GroupBy(death => death.UserId)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<PlaytimeDeathEvent>)group.ToList());
            var teamsByUser = demoTeams.GroupBy(change => change.UserId)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<PlaytimeTeamChangeEvent>)group.ToList());

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

                IReadOnlyList<PlaytimeSpawnEvent> otherSpawns = spawnsByUser.TryGetValue(user.UserId.Value, out var userSpawns)
                    ? userSpawns
                    : Array.Empty<PlaytimeSpawnEvent>();
                IReadOnlyList<PlaytimeDeathEvent> otherDeaths = deathsByUser.TryGetValue(user.UserId.Value, out var userDeaths)
                    ? userDeaths
                    : Array.Empty<PlaytimeDeathEvent>();
                IReadOnlyList<PlaytimeTeamChangeEvent> otherTeams = teamsByUser.TryGetValue(user.UserId.Value, out var userTeams)
                    ? userTeams
                    : Array.Empty<PlaytimeTeamChangeEvent>();

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

        CsvOutput.Write(filePath,
            new[]
            {
                "steam_id64",
                "steam_id",
                "name",
                "specs_you_seconds",
                "you_spec_seconds",
                "specs_you_demos",
                "you_spec_demos"
            },
            rows.Select(row => new string?[]
            {
                row.SteamId64?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                row.SteamId,
                row.Name,
                row.SpecsYouSeconds.ToString("0.##", CultureInfo.InvariantCulture),
                row.YouSpecSeconds.ToString("0.##", CultureInfo.InvariantCulture),
                row.SpecsYouDemoCount.ToString(CultureInfo.InvariantCulture),
                row.YouSpecDemoCount.ToString(CultureInfo.InvariantCulture)
            }),
            cancellationToken);

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

    private static List<Interval> BuildAliveIntervals(int userId, IReadOnlyList<PlaytimeSpawnEvent> spawns,
        IReadOnlyList<PlaytimeDeathEvent> deaths, IReadOnlyList<PlaytimeTeamChangeEvent> teamChanges, int demoEndTick)
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

    private static List<Interval> BuildSpectatorIntervals(int userId, IReadOnlyList<PlaytimeTeamChangeEvent> teamChanges,
        IReadOnlyList<PlaytimeSpawnEvent> spawns, int demoEndTick)
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

    private static string FormatHours(double seconds)
    {
        return HumanTime.FormatHours(seconds);
    }

    private static string FormatSteam(PeerRow row)
    {
        return row.SteamId64?.ToString() ?? row.SteamId;
    }

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
        private readonly NameCounter _names = new();

        public void TrackName(string name)
        {
            _names.Track(name);
        }

        public string DisplayName => _names.MostCommonOr("unknown");
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
