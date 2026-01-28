using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using CsvHelper;
using VaderSharp;

namespace TempusDemoArchive.Jobs;

public class ComputeUserSentimentJob : IJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        await using var db = new ArchiveDbContext();

        var analyzer = new SentimentIntensityAnalyzer();

        var aggregates = new Dictionary<(long? SteamId64, string SteamId), UserAggregate>();

        await foreach (var message in ArchiveQueries.ChatsWithUsers(db).AsAsyncEnumerable()
                           .WithCancellation(cancellationToken))
        {
            var steamId = message.SteamId ?? "unknown";
            var key = (message.SteamId64, steamId);

            if (!aggregates.TryGetValue(key, out var aggregate))
            {
                aggregate = new UserAggregate();
                aggregates[key] = aggregate;
            }

            aggregate.Sum += analyzer.PolarityScores(ArchiveUtils.GetMessageBody(message.Text)).Compound;
            aggregate.Count++;
            aggregate.TrackName(message.Name);
        }

        var results = aggregates
            .Select(entry => new UserSentiment
            {
                SteamId64 = entry.Key.SteamId64,
                SteamId = entry.Key.SteamId,
                Name = entry.Value.GetMostCommonName(),
                CompoundScore = entry.Value.Count == 0 ? 0 : entry.Value.Sum / entry.Value.Count,
                MessageCount = entry.Value.Count
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

    private sealed class UserAggregate
    {
        public double Sum { get; set; }
        public int Count { get; set; }
        private readonly NameCounter _names = new();

        public void TrackName(string name)
        {
            _names.Track(name);
        }

        public string GetMostCommonName()
        {
            return _names.MostCommonOr("unknown");
        }
    }
}

public class UserSentiment
{
    public required string Name { get; set; }
    public required string SteamId { get; set; }
    public long? SteamId64 { get; set; }
    public required double CompoundScore { get; set; }
    public int MessageCount { get; set; }
}
