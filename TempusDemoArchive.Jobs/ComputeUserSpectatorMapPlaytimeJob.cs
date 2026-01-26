using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace TempusDemoArchive.Jobs;

public class ComputeUserSpectatorMapPlaytimeJob : IJob
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
        var metaByDemo = await LoadMetaAsync(db, demoIds, cancellationToken);

        var teamChanges = await db.StvTeamChanges
            .AsNoTracking()
            .Join(userQuery,
                change => new { change.DemoId, UserId = (int?)change.UserId },
                user => new { user.DemoId, UserId = user.UserId },
                (change, _) => new TeamChangeEvent(change.DemoId, change.UserId, change.Tick, change.Team,
                    change.Disconnect))
            .ToListAsync(cancellationToken);

        var spawns = await db.StvSpawns
            .AsNoTracking()
            .Join(userQuery,
                spawn => new { spawn.DemoId, UserId = (int?)spawn.UserId },
                user => new { user.DemoId, UserId = user.UserId },
                (spawn, _) => new SpawnEvent(spawn.DemoId, spawn.UserId, spawn.Tick))
            .ToListAsync(cancellationToken);

        var teamsByDemo = teamChanges.GroupBy(change => change.DemoId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var spawnsByDemo = spawns.GroupBy(spawn => spawn.DemoId)
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

            if (!meta.IntervalPerTick.HasValue || meta.IntervalPerTick.Value <= 0)
            {
                continue;
            }

            IReadOnlyList<TeamChangeEvent> demoTeams = teamsByDemo.TryGetValue(demoId, out var teamList)
                ? teamList
                : Array.Empty<TeamChangeEvent>();
            IReadOnlyList<SpawnEvent> demoSpawns = spawnsByDemo.TryGetValue(demoId, out var spawnList)
                ? spawnList
                : Array.Empty<SpawnEvent>();

            var demoEndTick = GetDemoEndTick(meta, demoTeams, demoSpawns);
            if (demoEndTick <= 0)
            {
                continue;
            }

            var mapSeconds = 0d;
            foreach (var userId in userIds)
            {
                var spectatorIntervals = BuildSpectatorIntervals(userId, demoTeams, demoSpawns, demoEndTick);
                var seconds = spectatorIntervals.Sum(interval =>
                    (interval.EndTick - interval.StartTick) * meta.IntervalPerTick.Value);
                if (seconds <= 0)
                {
                    continue;
                }

                mapSeconds += seconds;
            }

            if (mapSeconds > 0)
            {
                if (!mapTotals.TryGetValue(meta.Map, out var totals))
                {
                    totals = new MapTotals();
                    mapTotals[meta.Map] = totals;
                }

                totals.SpectatorSeconds += mapSeconds;
                totals.DemoCount += 1;
                processedDemos += 1;
            }
        }

        if (mapTotals.Count == 0)
        {
            Console.WriteLine("No spectator time found for that user.");
            return;
        }

        var ordered = mapTotals
            .Select(entry => new MapTotalsRow(entry.Key, entry.Value.SpectatorSeconds, entry.Value.DemoCount))
            .OrderByDescending(row => row.SpectatorSeconds)
            .ToList();

        var fileName = ArchiveUtils.ToValidFileName($"map_spectator_time_{playerIdentifier}.csv");
        var filePath = Path.Combine(ArchivePath.TempRoot, fileName);
        await WriteCsvAsync(filePath, ordered, cancellationToken);

        Console.WriteLine($"Player: {displayName}");
        Console.WriteLine($"Demos processed: {processedDemos:N0}");
        Console.WriteLine($"CSV: {filePath}");
        Console.WriteLine();
        Console.WriteLine("Top 20 maps by spectator time:");

        foreach (var row in ordered.Take(20))
        {
            Console.WriteLine($"{row.Map} | spectator {FormatHours(row.SpectatorSeconds)} | demos {row.DemoCount}");
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

    private static int GetDemoEndTick(DemoMeta meta, IReadOnlyList<TeamChangeEvent> teamChanges,
        IReadOnlyList<SpawnEvent> spawns)
    {
        var demoEndTick = meta.HeaderTicks ?? 0;
        if (demoEndTick > 0)
        {
            return demoEndTick;
        }

        var max = 0;
        if (teamChanges.Count > 0)
        {
            max = Math.Max(max, teamChanges.Max(entry => entry.Tick));
        }

        if (spawns.Count > 0)
        {
            max = Math.Max(max, spawns.Max(entry => entry.Tick));
        }

        return max;
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

            if (entry.Tick > startTick)
            {
                intervals.Add(new Interval(startTick, entry.Tick));
            }

            spectating = false;
        }

        if (spectating && demoEndTick > startTick)
        {
            intervals.Add(new Interval(startTick, demoEndTick));
        }

        return intervals;
    }

    private static bool IsSpectatorTeam(string team)
    {
        return string.Equals(team, "spectator", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WriteCsvAsync(string path, IReadOnlyList<MapTotalsRow> rows,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.AppendLine("map,spectator_seconds,demo_count");
        foreach (var row in rows)
        {
            builder.Append(row.Map.Replace(",", " "));
            builder.Append(',');
            builder.Append(row.SpectatorSeconds.ToString("0.##", CultureInfo.InvariantCulture));
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
    private sealed record TeamChangeEvent(ulong DemoId, int UserId, int Tick, string Team, bool Disconnect);
    private sealed record SpawnEvent(ulong DemoId, int UserId, int Tick);
    private sealed record Interval(int StartTick, int EndTick);
    private sealed record SpectatorEvent(int Tick, SpectatorEventKind Kind);
    private sealed record MapTotalsRow(string Map, double SpectatorSeconds, int DemoCount);

    private sealed class MapTotals
    {
        public double SpectatorSeconds { get; set; }
        public int DemoCount { get; set; }
    }

    private enum SpectatorEventKind
    {
        Start = 0,
        End = 1
    }
}
