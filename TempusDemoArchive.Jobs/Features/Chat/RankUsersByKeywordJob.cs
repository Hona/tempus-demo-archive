using Microsoft.EntityFrameworkCore;

namespace TempusDemoArchive.Jobs;

public class RankUsersByKeywordJob : IJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        await using var db = new ArchiveDbContext();

        Console.WriteLine("Input words separated by commas: ");
        var input = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(input))
        {
            Console.WriteLine("No words provided.");
            return;
        }

        var words = input.Split(',').Select(x => x.Trim().ToLowerInvariant()).ToList();

        var matching = await ArchiveQueries.ChatsWithUsers(db)
            .Where(chat => words.Any(word => EF.Functions.Like(chat.Text, $"%{word}%")))
            .ToListAsync(cancellationToken: cancellationToken);

        var results = matching
            .GroupBy(x => new { x.SteamId64, x.SteamId, x.Name })
            .OrderByDescending(x => x.Count())
            .Select(match => new[]
            {
                string.IsNullOrWhiteSpace(match.Key.Name) ? "unknown" : match.Key.Name,
                match.Key.SteamId ?? "unknown",
                match.Key.SteamId64?.ToString() ?? "",
                match.Count().ToString()
            })
            .ToList();

        foreach (var result in results)
        {
            Console.WriteLine($"{result[0]} ({result[1]}) : {result[3]}");
        }

        var wordsKey = string.Join("_", words);
        var fileName = ArchiveUtils.ToValidFileName($"keyword_rank_{wordsKey}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv");
        var filePath = Path.Combine(ArchivePath.TempRoot, fileName);

        CsvOutput.Write(filePath,
            new[] { "Name", "SteamId", "SteamId64", "MatchCount" },
            results,
            cancellationToken);

        Console.WriteLine($"Wrote {filePath}");
    }
}
