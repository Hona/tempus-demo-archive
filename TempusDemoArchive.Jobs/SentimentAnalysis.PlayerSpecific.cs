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

        await using var db = new ArchiveDbContext();

        var analyzer = new SentimentIntensityAnalyzer();

        var userQuery = ArchiveQueries.SteamUserQuery(db, playerIdentifier);

        var results = await ArchiveQueries.ChatsWithUsers(db)
            .Join(userQuery,
                chat => new { chat.DemoId, SteamId64 = chat.SteamId64, SteamId = chat.SteamId ?? string.Empty },
                user => new { user.DemoId, user.SteamId64, SteamId = (string?)user.SteamIdClean ?? user.SteamId },
                (chat, _) => chat)
            .ToListAsync(cancellationToken);

        var output = results
            .Select(group =>
            {
                var messageOnly = ArchiveUtils.GetMessageBody(group.Text);
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

}

public class MessageSentiment
{
    public required string Text { get; set; }
    public required string FullText { get; set; }
    public required double CompoundScore { get; set; }
}
