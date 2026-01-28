using Microsoft.EntityFrameworkCore;

namespace TempusDemoArchive.Jobs;

internal static class PlaytimeEventLoader
{
    public static async Task<Dictionary<ulong, List<PlaytimeUserEntry>>> LoadUsersAsync(ArchiveDbContext db,
        IReadOnlyList<ulong> demoIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<ulong, List<PlaytimeUserEntry>>();
        foreach (var chunk in DbChunk.Chunk(demoIds))
        {
            var users = await db.StvUsers
                .AsNoTracking()
                .Where(user => chunk.Contains(user.DemoId))
                .Select(user => new PlaytimeUserEntry(user.DemoId, user.UserId, user.Name, user.SteamIdClean,
                    user.SteamId, user.SteamId64))
                .ToListAsync(cancellationToken);

            foreach (var user in users)
            {
                if (!result.TryGetValue(user.DemoId, out var list))
                {
                    list = new List<PlaytimeUserEntry>();
                    result[user.DemoId] = list;
                }

                list.Add(user);
            }
        }

        return result;
    }

    public static async Task<Dictionary<ulong, List<PlaytimeSpawnEvent>>> LoadSpawnsAsync(ArchiveDbContext db,
        IReadOnlyList<ulong> demoIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<ulong, List<PlaytimeSpawnEvent>>();
        foreach (var chunk in DbChunk.Chunk(demoIds))
        {
            var spawns = await db.StvSpawns
                .AsNoTracking()
                .Where(spawn => chunk.Contains(spawn.DemoId))
                .Select(spawn => new PlaytimeSpawnEvent(spawn.DemoId, spawn.UserId, spawn.Tick, spawn.Class,
                    spawn.Team))
                .ToListAsync(cancellationToken);

            foreach (var spawn in spawns)
            {
                if (!result.TryGetValue(spawn.DemoId, out var list))
                {
                    list = new List<PlaytimeSpawnEvent>();
                    result[spawn.DemoId] = list;
                }

                list.Add(spawn);
            }
        }

        return result;
    }

    public static async Task<Dictionary<ulong, List<PlaytimeDeathEvent>>> LoadDeathsAsync(ArchiveDbContext db,
        IReadOnlyList<ulong> demoIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<ulong, List<PlaytimeDeathEvent>>();
        foreach (var chunk in DbChunk.Chunk(demoIds))
        {
            var deaths = await db.StvDeaths
                .AsNoTracking()
                .Where(death => chunk.Contains(death.DemoId))
                .Select(death => new PlaytimeDeathEvent(death.DemoId, death.VictimUserId, death.Tick))
                .ToListAsync(cancellationToken);

            foreach (var death in deaths)
            {
                if (!result.TryGetValue(death.DemoId, out var list))
                {
                    list = new List<PlaytimeDeathEvent>();
                    result[death.DemoId] = list;
                }

                list.Add(death);
            }
        }

        return result;
    }

    public static async Task<Dictionary<ulong, List<PlaytimeTeamChangeEvent>>> LoadTeamChangesAsync(ArchiveDbContext db,
        IReadOnlyList<ulong> demoIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<ulong, List<PlaytimeTeamChangeEvent>>();
        foreach (var chunk in DbChunk.Chunk(demoIds))
        {
            var changes = await db.StvTeamChanges
                .AsNoTracking()
                .Where(change => chunk.Contains(change.DemoId))
                .Select(change => new PlaytimeTeamChangeEvent(change.DemoId, change.UserId, change.Tick, change.Team,
                    change.Disconnect))
                .ToListAsync(cancellationToken);

            foreach (var change in changes)
            {
                if (!result.TryGetValue(change.DemoId, out var list))
                {
                    list = new List<PlaytimeTeamChangeEvent>();
                    result[change.DemoId] = list;
                }

                list.Add(change);
            }
        }

        return result;
    }
}
