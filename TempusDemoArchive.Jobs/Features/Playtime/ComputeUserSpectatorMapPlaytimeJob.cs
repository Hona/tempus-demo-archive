using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace TempusDemoArchive.Jobs;

public class ComputeUserSpectatorMapPlaytimeJob : IJob
{
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
        var demoUserIds = resolvedUser.DemoUserIds;
        var demoIds = resolvedUser.DemoIds;
        var metaByDemo = await PlaytimeMetaLoader.LoadByDemoIdAsync(db, demoIds, cancellationToken);

        var userQuery = ArchiveQueries.SteamUserQuery(db, playerIdentifier)
            .AsNoTracking()
            .Where(user => user.UserId != null);

        var teamChanges = await db.StvTeamChanges
            .AsNoTracking()
            .Join(userQuery,
                change => new { change.DemoId, UserId = (int?)change.UserId },
                user => new { user.DemoId, UserId = user.UserId },
                (change, _) => new PlaytimeTeamChangeEvent(change.DemoId, change.UserId, change.Tick, change.Team,
                    change.Disconnect))
            .ToListAsync(cancellationToken);

        var spawns = await db.StvSpawns
            .AsNoTracking()
            .Join(userQuery,
                spawn => new { spawn.DemoId, UserId = (int?)spawn.UserId },
                user => new { user.DemoId, UserId = user.UserId },
                (spawn, _) => new PlaytimeSpawnEvent(spawn.DemoId, spawn.UserId, spawn.Tick, spawn.Class, spawn.Team))
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

            IReadOnlyList<PlaytimeTeamChangeEvent> demoTeams = teamsByDemo.TryGetValue(demoId, out var teamList)
                ? teamList
                : Array.Empty<PlaytimeTeamChangeEvent>();
            IReadOnlyList<PlaytimeSpawnEvent> demoSpawns = spawnsByDemo.TryGetValue(demoId, out var spawnList)
                ? spawnList
                : Array.Empty<PlaytimeSpawnEvent>();

            var demoEndTick = PlaytimeCalculator.GetDemoEndTick(meta, demoSpawns, Array.Empty<PlaytimeDeathEvent>(),
                demoTeams);
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

        CsvOutput.Write(filePath,
            new[] { "map", "spectator_seconds", "demo_count" },
            ordered.Select(row => new string?[]
            {
                row.Map,
                row.SpectatorSeconds.ToString("0.##", CultureInfo.InvariantCulture),
                row.DemoCount.ToString(CultureInfo.InvariantCulture)
            }),
            cancellationToken);

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

    private static string FormatHours(double seconds)
    {
        return HumanTime.FormatHours(seconds);
    }

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
