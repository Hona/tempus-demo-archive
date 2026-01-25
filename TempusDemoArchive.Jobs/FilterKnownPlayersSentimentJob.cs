using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;

namespace TempusDemoArchive.Jobs;

public class FilterKnownPlayersSentimentJob : IJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var http = new HttpClient();
        var playerList =
            await http.GetStringAsync(
                "https://raw.githubusercontent.com/ultralaser1986/TempusArchive/main/data/players.list", cancellationToken);

        var playerNames = playerList.Split('\n')
            .Select(x => x.Split(" ").Last().ToLower())
            .ToList();
        
        var fileName = "sentiment_analysis.csv";
        var filePath = Path.Combine(ArchivePath.TempRoot, fileName);
        
        await using var stream = File.OpenRead(filePath);
        
        var reader = new CsvReader(new StreamReader(stream), new CsvConfiguration(CultureInfo.InvariantCulture));
        var records = reader.GetRecords<UserSentiment>().ToList();
        
        var knownPlayers = records
            .Where(x => playerNames.Contains(x.Name.Trim(), StringComparer.OrdinalIgnoreCase))
            .GroupBy(x => x.Name.Trim().ToLower())
            .Select(x => new UserSentiment()
            {
                Name = x.Key,
                SteamId = x.FirstOrDefault()?.SteamId ?? string.Empty,
                SteamId64 = x.FirstOrDefault()?.SteamId64,
                CompoundScore = x.Average(y => y.CompoundScore)
            })
            .OrderByDescending(x => x.CompoundScore)
            .DistinctBy(x => x.Name)
            .ToList();
        
        var knownPlayersFileName = "known_players_sentiment_analysis.csv";
        var knownPlayersFilePath = Path.Combine(ArchivePath.TempRoot, knownPlayersFileName);
        await using var stream2 = File.OpenWrite(knownPlayersFilePath);
        await using var writer = new StreamWriter(stream2);
        await using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
        await csv.WriteRecordsAsync(knownPlayers, cancellationToken);
    }
}
