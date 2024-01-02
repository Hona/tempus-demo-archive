namespace TempusDemoArchive.Jobs;

public class MapNamesThatContain : IJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine("Maps might be renamed over time - so this will return all STV maps that contain the input string");
        Console.WriteLine("Input Map name: ");
        var mapName = Console.ReadLine()?.Trim();
        
        await using var db = new ArchiveDbContext();
        
        // Unique map name only
        var matchingMapNames = db.Stvs
            .Select(x => x.Header.Map)
            .Where(x => x.Contains(mapName))
            .Distinct()
            .ToList();
        
        foreach (var map in matchingMapNames)
        {
            Console.WriteLine(map);
        }
        
        Console.WriteLine($"Found {matchingMapNames.Count} maps");
    }
}