using Microsoft.EntityFrameworkCore;

namespace TempusDemoArchive.Jobs;

internal static class PlaytimeUserResolver
{
    public static async Task<ResolvedPlaytimeUser?> ResolveAsync(ArchiveDbContext db, string playerIdentifier,
        CancellationToken cancellationToken)
    {
        var userQuery = ArchiveQueries.SteamUserQuery(db, playerIdentifier)
            .AsNoTracking()
            .Where(user => user.UserId != null);

        var userEntries = await userQuery
            .Select(user => new { user.DemoId, UserId = user.UserId!.Value, user.Name })
            .ToListAsync(cancellationToken);

        if (userEntries.Count == 0)
        {
            return null;
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

        return new ResolvedPlaytimeUser(playerIdentifier, displayName, demoUserIds, demoUserIds.Keys.ToList());
    }
}

internal sealed record ResolvedPlaytimeUser(
    string Identifier,
    string DisplayName,
    Dictionary<ulong, HashSet<int>> DemoUserIds,
    List<ulong> DemoIds);
