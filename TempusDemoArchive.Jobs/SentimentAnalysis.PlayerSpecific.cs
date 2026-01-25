using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using VaderSharp;

namespace TempusDemoArchive.Jobs;

public class SentimentAnalysis_PlayerSpecificJob : IJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var playerName = "minty";
        
        await using var db = new ArchiveDbContext();

        var analyzer = new SentimentIntensityAnalyzer();

        // Lets get per steam user, per year sentiment analysis.
        // Each chat message gets the compound score then is averaged in the yearly groupings.
        // Select out the year, steamID, last known name, and average compound score.

        var results = db.StvChats
            .Where(x => !x.Text.StartsWith("Tip |"))
            .Where(x => x.Text.StartsWith("* [") || x.Text.StartsWith("["))
            .Where(x => EF.Functions.Like(x.Text, $"%{playerName + " : "}%"))
            .ToList()
            .Select(group =>
            {
                var messageOnly = group.Text.Split(" : ", 2)[1];
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

        for (var index = 0; index < results.Count; index++)
        {
            var result = results[index];
            
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