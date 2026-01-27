using System.Text;
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

        var sb = new StringBuilder();
        
        foreach (var match in matching
                     .GroupBy(x => new { x.SteamId64, x.SteamId, x.Name })
                     .OrderByDescending(x => x.Count()))
        {
            var key = match.Key;
            var steamId = key.SteamId ?? "unknown";
            var name = string.IsNullOrWhiteSpace(key.Name) ? "unknown" : key.Name;

            sb.AppendLine($"{name} ({steamId}) : {match.Count()}");
        }
        
        var text = sb.ToString();
        
        Console.WriteLine(text);
        
        var wordsKey = string.Join("_", words);
        var fileName = ArchiveUtils.ToValidFileName($"keyword_rank_{wordsKey}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.txt");
        
        var filePath = Path.Combine(ArchivePath.TempRoot, fileName);
        
        await File.WriteAllTextAsync(filePath, text, cancellationToken);
    }
}
