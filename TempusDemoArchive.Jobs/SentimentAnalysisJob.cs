using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using CsvHelper;
using VaderSharp;

namespace TempusDemoArchive.Jobs;

public class SentimentAnalysisJob : IJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        await using var db = new ArchiveDbContext();

        var analyzer = new SentimentIntensityAnalyzer();

        // Lets get per steam user, per year sentiment analysis.
        // Each chat message gets the compound score then is averaged in the yearly groupings.
        // Select out the year, steamID, last known name, and average compound score.

        var results = db.StvChats
            .Where(x => !x.Text.StartsWith("Tip |"))
            .Where(x => x.Text.StartsWith("* [") || x.Text.StartsWith("["))
            .Where(x => x.Text.Contains(" : "))
            .ToList()
            .GroupBy(x => x.Text.Split(']', 2).Last().Split(":", 2, StringSplitOptions.RemoveEmptyEntries).First())
            .Select(group => new UserSentiment()
            {
                Name = group.Key,
                CompoundScore = group.Average(chat => analyzer.PolarityScores(chat.Text.Split(" : ", 2)[1]).Compound)
            })
            .OrderByDescending(x => x.CompoundScore)
            .ToList();

        // Save the results to a file
        var fileName = "sentiment_analysis.csv";
        var filePath = Path.Combine(ArchivePath.TempRoot, fileName);
        await using var stream = File.OpenWrite(filePath);
        await using var writer = new StreamWriter(stream);
        await using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
        await csv.WriteRecordsAsync(results, cancellationToken);
    }
}

public class UserSentiment
{
    public required string Name { get; set; }
    public required double CompoundScore { get; set; }
}