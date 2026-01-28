using Microsoft.EntityFrameworkCore;

namespace TempusDemoArchive.Jobs;

internal static class PlaytimeMetaLoader
{
    public static async Task<Dictionary<ulong, PlaytimeDemoMeta>> LoadByDemoIdAsync(ArchiveDbContext db,
        IReadOnlyList<ulong> demoIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<ulong, PlaytimeDemoMeta>();
        foreach (var chunk in DbChunk.Chunk(demoIds))
        {
            var metas = await db.Stvs
                .AsNoTracking()
                .Where(stv => chunk.Contains(stv.DemoId))
                .Select(stv => new PlaytimeDemoMeta(stv.DemoId, stv.Header.Map, stv.Header.Server, stv.IntervalPerTick,
                    stv.Header.Ticks))
                .ToListAsync(cancellationToken);

            foreach (var meta in metas)
            {
                result[meta.DemoId] = meta;
            }
        }

        return result;
    }
}
