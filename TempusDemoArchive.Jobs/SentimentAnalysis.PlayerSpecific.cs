using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using VaderSharp;

namespace TempusDemoArchive.Jobs;

public class SentimentAnalysis_PlayerSpecificJob : IJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine("Enter steam ID or Steam64:");
        var playerIdentifier = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(playerIdentifier))
        {
            Console.WriteLine("No identifier provided.");
            return;
        }

        var steam64 = long.TryParse(playerIdentifier, out var parsed) ? parsed : (long?)null;
        
        await using var db = new ArchiveDbContext();

        var analyzer = new SentimentIntensityAnalyzer();

        var userQuery = db.StvUsers
            .Where(user => user.UserId != null)
            .Where(user => user.SteamId == playerIdentifier || user.SteamIdClean == playerIdentifier
                           || (steam64 != null && user.SteamId64 == steam64));

        var results = await db.StvChats
            .Where(chat => chat.FromUserId != null)
            .Join(userQuery,
                chat => new { chat.DemoId, UserId = chat.FromUserId!.Value },
                user => new { user.DemoId, UserId = user.UserId!.Value },
                (chat, user) => new { chat.Text, user.Name })
            .ToListAsync(cancellationToken);

        var output = results
            .Select(group =>
            {
                var messageOnly = GetMessageBody(group.Text);
                return new MessageSentiment
                {
                    Text = messageOnly,
                    FullText = group.Text,
                    CompoundScore = analyzer.PolarityScores(messageOnly).Compound
                };
            })
            .OrderBy/*Descending*/(x => x.CompoundScore)
            .Take(200)
            .ToList();

        for (var index = 0; index < output.Count; index++)
        {
            var result = output[index];
            
            Console.WriteLine($"#{index} = {result.CompoundScore} | '{result.Text}'");
            Console.WriteLine(result.FullText);
            Console.WriteLine();
        }
    }

    private static string GetMessageBody(string text)
    {
        var marker = " : ";
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        return index >= 0 ? text[(index + marker.Length)..] : text;
    }
}

public class MessageSentiment
{
    public required string Text { get; set; }
    public required string FullText { get; set; }
    public required double CompoundScore { get; set; }
}
