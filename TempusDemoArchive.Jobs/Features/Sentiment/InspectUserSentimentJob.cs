using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using VaderSharp;

namespace TempusDemoArchive.Jobs;

public class InspectUserSentimentJob : IJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var playerIdentifier = JobPrompts.ReadSteamIdentifier();
        if (playerIdentifier == null)
        {
            return;
        }

        await using var db = new ArchiveDbContext();

        var analyzer = new SentimentIntensityAnalyzer();

        var results = await ArchiveQueries.ChatsForUser(db, playerIdentifier)
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
