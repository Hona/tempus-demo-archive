using System.Text.Json;
using System.Text.RegularExpressions;
using SQLitePCL;

namespace TempusDemoArchive.Jobs;

public class TESTINGWrHistoryJob : IJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine("Input map name: ");
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
        
        await using var db = new ArchiveDbContext();
        
        const string MapWrPattern = @"^Tempus \| \(([^)]+)\) (.*?) beat the map record: (\d{2}:\d{2}\.\d{2}) \((WR -)?(-?\d{2}:\d{2}\.\d{2})\) \| ((?:-)?\d{2}:\d{2}\.\d{2}) improvement!$";

        var mapsMatching = db.Stvs
            .Where(x => x.Header.Map == map)
            .Select(x => x.Header.Map)
            .Distinct()
            .ToList();

        foreach (var matchingMap in mapsMatching)
        {
            Console.WriteLine(matchingMap);
        }
        
        var suspectedWrMessages = db.Stvs
            .Where(x => x.Header.Map == map || x.Header.Map.Contains(map + "_"))
            .SelectMany(x => x.Chats)
            .Select(x => new {x.Text, x.DemoId})
            .Where(x => x.Text.StartsWith("Tempus | ("))
            .Where(x => x.Text.Contains(" beat the map record: "));
        
        var suspectedWrMessagesList = suspectedWrMessages.ToList();
        
        // No SQL-side regex, so gotta do it in memory
        var wrMessages = suspectedWrMessagesList
            .Select(x => new {Match = Regex.Match(x.Text, MapWrPattern), x.DemoId})
            .Where(x => x.Match.Success)
            .ToList();

        const string soldier = "Solly";
        const string demoman = "Demo";

        var output = new List<WrHistoryEntry>();
        foreach (var tuple in wrMessages)
        {
            var match = tuple.Match;
            
            var detectedClass = match.Groups[1].Value;
            var player = match.Groups[2].Value;
            var time = match.Groups[3].Value;
            var wrSplit = match.Groups[4].Value;
            var prSplit = match.Groups[5].Value;
            
            var date = await db.Demos
                .Where(x => x.Id == tuple.DemoId)
                .Select(x => ArchiveUtils.GetDateFromTimestamp(x.Date))
                .FirstOrDefaultAsync(cancellationToken);
            
            var identity = await ResolveUserIdentityAsync(db, tuple.DemoId, player, cancellationToken);
            var entry = new WrHistoryEntry(player, detectedClass, time, wrSplit, prSplit, date, tuple.DemoId,
                identity?.SteamId64, identity?.SteamId);
            output.Add(entry);
        }
        
        // Now add in any IRC messages that were missed
        var ircRegex =
            @"^:: \(([^)]+)\) ([^ ]+) broke ([^ ]+) WR: (\d{2}:\d{2}\.\d{2}) \((?:WR -)?(-?\d{2}:\d{2}\.\d{2})\)!$";
        
        // They are multi line
        var suspectedIrcWrMessages = db.StvChats
            .Where(x => x.Text.Contains(":: ("))
            .Where(x => x.Text.Contains(" broke "))
            .Where(x => x.Text.Contains(" WR: "))
            .Where(x => x.Text.Contains(" " + map + " "));
        
        var suspectedIrcWrMessagesList = suspectedIrcWrMessages.ToList();
        
        // No SQL-side regex, so gotta do it in memory
        var ircWrMessages = suspectedIrcWrMessagesList
            .Select(x => new {Match = Regex.Match(x.Text.Split('\n').Last(), ircRegex), x.DemoId})
            .Where(x => x.Match.Success)
            .ToList();
        
        foreach (var tuple in ircWrMessages)
        {
            var match = tuple.Match;
            
            var detectedClass = match.Groups[1].Value;
            var player = match.Groups[2].Value;
            var time = match.Groups[4].Value;
            var wrSplit = match.Groups[5].Value;
            var prSplit = match.Groups[5].Value;
            
            var date = await db.Demos
                .Where(x => x.Id == tuple.DemoId)
                .Select(x => ArchiveUtils.GetDateFromTimestamp(x.Date))
                .FirstOrDefaultAsync(cancellationToken);
            
            var identity = await ResolveUserIdentityAsync(db, tuple.DemoId, player, cancellationToken);
            var entry = new WrHistoryEntry(player, detectedClass, time, wrSplit, prSplit, date, tuple.DemoId,
                identity?.SteamId64, identity?.SteamId);
            output.Add(entry);
        }
        
        
        var orderedOutput = output
            .OrderByDescending(x => x.Time)
            .ToList();

        var classOutput = @class switch
        {
            "S" => orderedOutput.Where(x => x.Class == soldier),
            "D" => orderedOutput.Where(x => x.Class == demoman),
            _ => Enumerable.Empty<WrHistoryEntry>()
        };
        
        foreach (var wrHistoryEntry in classOutput.OrderBy(x => x.Date))
        {
            var date = ArchiveUtils.FormatDate(wrHistoryEntry.Date);
            var steam = wrHistoryEntry.SteamId64?.ToString() ?? wrHistoryEntry.SteamId ?? "unknown";
            Console.WriteLine($"{date} - {wrHistoryEntry.Time} - {wrHistoryEntry.Player} ({wrHistoryEntry.DemoId}) [{steam}]");
        }
    }

    private static async Task<UserIdentity?> ResolveUserIdentityAsync(ArchiveDbContext db, ulong demoId, string player,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(player))
        {
            return null;
        }

        var matches = await db.StvUsers
            .Where(user => user.DemoId == demoId)
            .Where(user => EF.Functions.Like(user.Name, player))
            .ToListAsync(cancellationToken);

        if (matches.Count != 1)
        {
            return null;
        }

        var match = matches[0];
        return new UserIdentity(match.SteamId64, match.SteamIdClean ?? match.SteamId);
    }
}
public record WrHistoryEntry(string Player, string Class, string Time, string WrSplit, string PrSplit,
    DateTime? Date = null, ulong? DemoId = null, long? SteamId64 = null, string? SteamId = null);

public record UserIdentity(long? SteamId64, string? SteamId);
