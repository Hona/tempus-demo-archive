using System.Text;

namespace TempusDemoArchive.Jobs;

public class RankNaughtyWords : IJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        await using var db = new ArchiveDbContext();

        Console.WriteLine("Input naughty word: ");
        var foundMessage = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(foundMessage))
        {
            Console.WriteLine("No word provided.");
            return;
        }
        
        var matching = await db.StvChats
            .Where(x => !x.Text.StartsWith("Tip |"))
            .Where(x => x.Text.StartsWith("* [") || x.Text.StartsWith("["))
            .Where(x => EF.Functions.Like(x.Text, $"%]%:%{foundMessage}%"))
            .ToListAsync(cancellationToken: cancellationToken);

        var sb = new StringBuilder();
        
        foreach (var match in matching
                     .GroupBy(x => x.Text.Split(']', 2).Last()
                         .Split(":", 2, StringSplitOptions.RemoveEmptyEntries).First())
                     .OrderByDescending(x => x.Count()))
        {
            var playerName = match.Key;

            sb.AppendLine(playerName + " : " + match.Count());
        }
        
        var text = sb.ToString();
        
        Console.WriteLine(text);
        
        var fileName = $"naughty_words_{FindExactMessage.ToValidFileName(foundMessage)}.txt";
        
        var filePath = Path.Combine(ArchivePath.TempRoot, fileName);
        
        await File.WriteAllTextAsync(filePath, text, cancellationToken);
    }
}
