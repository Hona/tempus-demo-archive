using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TempusDemoArchive.Persistence.Models.STVs;

namespace TempusDemoArchive.Jobs;

public class ComputeUserMapPlaytimeJob : IJob
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
        var demoMeta = await db.Stvs
            .AsNoTracking()
            .Where(stv => demoIds.Contains(stv.DemoId))
            .Select(stv => new PlaytimeDemoMeta(stv.DemoId, stv.Header.Map, string.Empty, stv.IntervalPerTick,
                stv.Header.Ticks))
            .ToListAsync(cancellationToken);
        var metaByDemo = demoMeta.ToDictionary(meta => meta.DemoId, meta => meta);

        var spawns = await db.StvSpawns
            .AsNoTracking()
            .Join(userQuery,
                spawn => new { spawn.DemoId, UserId = (int?)spawn.UserId },
                user => new { user.DemoId, UserId = user.UserId },
                (spawn, _) => new PlaytimeSpawnEvent(spawn.DemoId, spawn.UserId, spawn.Tick, spawn.Class, spawn.Team))
            .ToListAsync(cancellationToken);

        var deaths = await db.StvDeaths
            .AsNoTracking()
            .Join(userQuery,
                death => new { death.DemoId, UserId = (int?)death.VictimUserId },
                user => new { user.DemoId, UserId = user.UserId },
                (death, _) => new PlaytimeDeathEvent(death.DemoId, death.VictimUserId, death.Tick))
            .ToListAsync(cancellationToken);

        var teamChanges = await db.StvTeamChanges
            .AsNoTracking()
            .Join(userQuery,
                change => new { change.DemoId, UserId = (int?)change.UserId },
                user => new { user.DemoId, UserId = user.UserId },
                (change, _) => new PlaytimeTeamChangeEvent(change.DemoId, change.UserId, change.Tick, change.Team,
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

            IReadOnlyList<PlaytimeSpawnEvent> demoSpawns = spawnsByDemo.TryGetValue(demoId, out var spawnList)
                ? spawnList
                : Array.Empty<PlaytimeSpawnEvent>();
            IReadOnlyList<PlaytimeDeathEvent> demoDeaths = deathsByDemo.TryGetValue(demoId, out var deathList)
                ? deathList
                : Array.Empty<PlaytimeDeathEvent>();
            IReadOnlyList<PlaytimeTeamChangeEvent> demoTeams = teamsByDemo.TryGetValue(demoId, out var teamList)
                ? teamList
                : Array.Empty<PlaytimeTeamChangeEvent>();

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

    private static bool ComputeDemoTotals(PlaytimeDemoMeta meta, HashSet<int> userIds,
        IReadOnlyList<PlaytimeSpawnEvent> spawns,
        IReadOnlyList<PlaytimeDeathEvent> deaths,
        IReadOnlyList<PlaytimeTeamChangeEvent> teamChanges,
        MapTotals totals)
    {
        if (!PlaytimeCalculator.TryComputeDemoTotals(meta, userIds, spawns, deaths, teamChanges, out var demoTotals))
        {
            return false;
        }

        if (demoTotals.TotalSeconds <= 0)
        {
            return false;
        }

        totals.SoldierSeconds += demoTotals.SoldierSeconds;
        totals.DemoSeconds += demoTotals.DemoSeconds;
        totals.TotalSeconds += demoTotals.TotalSeconds;
        totals.DemoCount += 1;
        return true;
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
        return HumanTime.FormatHours(seconds);
    }

    private sealed record UserEntry(ulong DemoId, int UserId, string Name);
    private sealed record MapTotalsRow(string Map, double SoldierSeconds, double DemoSeconds, double TotalSeconds,
        int DemoCount);

    private sealed class MapTotals
    {
        public double SoldierSeconds { get; set; }
        public double DemoSeconds { get; set; }
        public double TotalSeconds { get; set; }
        public int DemoCount { get; set; }
    }

}
