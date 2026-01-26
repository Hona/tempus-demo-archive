using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TempusDemoArchive.Persistence.Models.STVs;

namespace TempusDemoArchive.Jobs;

public class ComputeUserMapPlaytimeJob : IJob
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

        var userEntries = await userQuery
            .Select(user => new UserEntry(user.DemoId, user.UserId!.Value, user.Name))
            .ToListAsync(cancellationToken);

        if (userEntries.Count == 0)
        {
            Console.WriteLine("No demos found for that user.");
            return;
        }

        var displayName = userEntries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key)
            .FirstOrDefault() ?? playerIdentifier;

        var demoUserIds = new Dictionary<ulong, HashSet<int>>();
        foreach (var entry in userEntries)
        {
            if (!demoUserIds.TryGetValue(entry.DemoId, out var ids))
            {
                ids = new HashSet<int>();
                demoUserIds[entry.DemoId] = ids;
            }

            ids.Add(entry.UserId);
        }

        var demoIds = demoUserIds.Keys.ToList();
        var demoMeta = await db.Stvs
            .AsNoTracking()
            .Where(stv => demoIds.Contains(stv.DemoId))
            .Select(stv => new DemoMeta(stv.DemoId, stv.Header.Map, stv.IntervalPerTick, stv.Header.Ticks))
            .ToListAsync(cancellationToken);
        var metaByDemo = demoMeta.ToDictionary(meta => meta.DemoId, meta => meta);

        var spawns = await db.StvSpawns
            .AsNoTracking()
            .Join(userQuery,
                spawn => new { spawn.DemoId, UserId = (int?)spawn.UserId },
                user => new { user.DemoId, UserId = user.UserId },
                (spawn, _) => new SpawnEvent(spawn.DemoId, spawn.UserId, spawn.Tick, spawn.Class, spawn.Team))
            .ToListAsync(cancellationToken);

        var deaths = await db.StvDeaths
            .AsNoTracking()
            .Join(userQuery,
                death => new { death.DemoId, UserId = (int?)death.VictimUserId },
                user => new { user.DemoId, UserId = user.UserId },
                (death, _) => new DeathEvent(death.DemoId, death.VictimUserId, death.Tick))
            .ToListAsync(cancellationToken);

        var teamChanges = await db.StvTeamChanges
            .AsNoTracking()
            .Join(userQuery,
                change => new { change.DemoId, UserId = (int?)change.UserId },
                user => new { user.DemoId, UserId = user.UserId },
                (change, _) => new TeamChangeEvent(change.DemoId, change.UserId, change.Tick, change.Team,
                    change.Disconnect))
            .ToListAsync(cancellationToken);

        var spawnsByDemo = spawns.GroupBy(spawn => spawn.DemoId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var deathsByDemo = deaths.GroupBy(death => death.DemoId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var teamsByDemo = teamChanges.GroupBy(change => change.DemoId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var mapTotals = new Dictionary<string, MapTotals>(StringComparer.OrdinalIgnoreCase);
        var processedDemos = 0;
        foreach (var demoId in demoIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!metaByDemo.TryGetValue(demoId, out var meta))
            {
                continue;
            }

            if (!demoUserIds.TryGetValue(demoId, out var userIds))
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

            var totals = mapTotals.TryGetValue(meta.Map, out var existing)
                ? existing
                : mapTotals[meta.Map] = new MapTotals();

            if (ComputeDemoTotals(meta, userIds, demoSpawns, demoDeaths, demoTeams, totals))
            {
                processedDemos++;
            }
        }

        if (mapTotals.Count == 0)
        {
            Console.WriteLine("No playtime found for that user.");
            return;
        }

        var ordered = mapTotals
            .Select(entry => new MapTotalsRow(entry.Key, entry.Value.SoldierSeconds, entry.Value.DemoSeconds,
                entry.Value.TotalSeconds, entry.Value.DemoCount))
            .OrderByDescending(row => row.TotalSeconds)
            .ToList();

        var fileName = ArchiveUtils.ToValidFileName($"map_playtime_{playerIdentifier}.csv");
        var filePath = Path.Combine(ArchivePath.TempRoot, fileName);
        await WriteCsvAsync(filePath, ordered, cancellationToken);

        Console.WriteLine($"Player: {displayName}");
        Console.WriteLine($"Demos processed: {processedDemos:N0}");
        Console.WriteLine($"CSV: {filePath}");
        Console.WriteLine();
        Console.WriteLine("Top 20 maps by soldier+demo playtime:");

        foreach (var row in ordered.Take(20))
        {
            Console.WriteLine(
                $"{row.Map} | solly {FormatHours(row.SoldierSeconds)} | demo {FormatHours(row.DemoSeconds)} | total {FormatHours(row.TotalSeconds)} | demos {row.DemoCount}");
        }
    }

    private static bool ComputeDemoTotals(DemoMeta meta, HashSet<int> userIds,
        IReadOnlyList<SpawnEvent> spawns, IReadOnlyList<DeathEvent> deaths, IReadOnlyList<TeamChangeEvent> teamChanges,
        MapTotals totals)
    {
        if (!meta.IntervalPerTick.HasValue || meta.IntervalPerTick.Value <= 0)
        {
            return false;
        }

        var demoEndTick = meta.HeaderTicks ?? 0;
        if (demoEndTick <= 0)
        {
            demoEndTick = Math.Max(GetMaxTick(spawns), Math.Max(GetMaxTick(deaths), GetMaxTick(teamChanges)));
        }

        if (demoEndTick <= 0)
        {
            return false;
        }

        var hadTime = false;
        foreach (var userId in userIds)
        {
            var playerEvents = BuildPlayerEvents(userId, spawns, deaths, teamChanges);
            if (playerEvents.Count == 0)
            {
                continue;
            }

            hadTime |= AccumulateEvents(playerEvents, demoEndTick, meta.IntervalPerTick.Value, totals);
        }

        if (hadTime)
        {
            totals.DemoCount += 1;
        }

        return hadTime;
    }

    private static List<PlayerEvent> BuildPlayerEvents(int userId, IReadOnlyList<SpawnEvent> spawns,
        IReadOnlyList<DeathEvent> deaths, IReadOnlyList<TeamChangeEvent> teamChanges)
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
            if (change.UserId == userId && IsSpectatorEvent(change))
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

        return events;
    }

    private static bool AccumulateEvents(IReadOnlyList<PlayerEvent> events, int demoEndTick, double intervalPerTick,
        MapTotals totals)
    {
        var alive = false;
        var currentClass = string.Empty;
        var startTick = 0;
        var hasTime = false;

        foreach (var entry in events)
        {
            if (entry.Kind == EventKind.Spawn)
            {
                if (alive)
                {
                    hasTime |= AddInterval(totals, currentClass, startTick, entry.Tick, intervalPerTick);
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

            hasTime |= AddInterval(totals, currentClass, startTick, entry.Tick, intervalPerTick);
            alive = false;
        }

        if (alive)
        {
            hasTime |= AddInterval(totals, currentClass, startTick, demoEndTick, intervalPerTick);
        }

        return hasTime;
    }

    private static bool AddInterval(MapTotals totals, string className, int startTick, int endTick,
        double intervalPerTick)
    {
        var tickDelta = endTick - startTick;
        if (tickDelta <= 0)
        {
            return false;
        }

        var seconds = tickDelta * intervalPerTick;
        if (seconds <= 0)
        {
            return false;
        }

        var isSoldier = string.Equals(className, "soldier", StringComparison.OrdinalIgnoreCase);
        var isDemo = string.Equals(className, "demoman", StringComparison.OrdinalIgnoreCase);
        if (!isSoldier && !isDemo)
        {
            return false;
        }

        totals.TotalSeconds += seconds;
        if (isSoldier)
        {
            totals.SoldierSeconds += seconds;
        }
        else if (isDemo)
        {
            totals.DemoSeconds += seconds;
        }

        return true;
    }

    private static bool IsPlayableClass(string className)
    {
        return !string.IsNullOrWhiteSpace(className) && PlayableClasses.Contains(className);
    }

    private static bool IsSpectatorEvent(TeamChangeEvent change)
    {
        if (change.Disconnect)
        {
            return true;
        }

        return string.Equals(change.Team, "spectator", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetMaxTick(IReadOnlyList<SpawnEvent> events)
    {
        return events.Count == 0 ? 0 : events.Max(entry => entry.Tick);
    }

    private static int GetMaxTick(IReadOnlyList<DeathEvent> events)
    {
        return events.Count == 0 ? 0 : events.Max(entry => entry.Tick);
    }

    private static int GetMaxTick(IReadOnlyList<TeamChangeEvent> events)
    {
        return events.Count == 0 ? 0 : events.Max(entry => entry.Tick);
    }

    private static async Task WriteCsvAsync(string path, IReadOnlyList<MapTotalsRow> rows,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.AppendLine("map,solly_seconds,demo_seconds,total_seconds,demo_count");
        foreach (var row in rows)
        {
            builder.Append(row.Map.Replace(",", " "));
            builder.Append(',');
            builder.Append(row.SoldierSeconds.ToString("0.##", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(row.DemoSeconds.ToString("0.##", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(row.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(row.DemoCount.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine();
        }

        await File.WriteAllTextAsync(path, builder.ToString(), cancellationToken);
    }

    private static string FormatHours(double seconds)
    {
        return (seconds / 3600d).ToString("0.##", CultureInfo.InvariantCulture) + "h";
    }

    private sealed record UserEntry(ulong DemoId, int UserId, string Name);
    private sealed record DemoMeta(ulong DemoId, string Map, double? IntervalPerTick, int? HeaderTicks);
    private sealed record SpawnEvent(ulong DemoId, int UserId, int Tick, string Class, string Team);
    private sealed record DeathEvent(ulong DemoId, int UserId, int Tick);
    private sealed record TeamChangeEvent(ulong DemoId, int UserId, int Tick, string Team, bool Disconnect);
    private sealed record PlayerEvent(int Tick, EventKind Kind, string? Class);
    private sealed record MapTotalsRow(string Map, double SoldierSeconds, double DemoSeconds, double TotalSeconds,
        int DemoCount);

    private sealed class MapTotals
    {
        public double SoldierSeconds { get; set; }
        public double DemoSeconds { get; set; }
        public double TotalSeconds { get; set; }
        public int DemoCount { get; set; }
    }

    private enum EventKind
    {
        Spectator = 0,
        Death = 1,
        Spawn = 2
    }
}
