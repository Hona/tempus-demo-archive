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

        var messages = await db.StvChats
            .Where(chat => chat.FromUserId != null)
            .Join(db.StvUsers.Where(user => user.UserId != null),
                chat => new { chat.DemoId, UserId = chat.FromUserId!.Value },
                user => new { user.DemoId, UserId = user.UserId!.Value },
                (chat, user) => new ChatWithUser
                {
                    Text = chat.Text,
                    Name = user.Name,
                    SteamId64 = user.SteamId64,
                    SteamId = user.SteamIdClean ?? user.SteamId
                })
            .ToListAsync(cancellationToken);

        var results = messages
            .GroupBy(message => new { message.SteamId64, message.SteamId })
            .Select(group => new UserSentiment
            {
                SteamId64 = group.Key.SteamId64,
                SteamId = group.Key.SteamId ?? string.Empty,
                Name = GetMostCommonName(group),
                CompoundScore = group.Average(chat => analyzer.PolarityScores(GetMessageBody(chat.Text)).Compound),
                MessageCount = group.Count()
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

    private static string GetMessageBody(string text)
    {
        var marker = " : ";
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        return index >= 0 ? text[(index + marker.Length)..] : text;
    }

    private static string GetMostCommonName(IEnumerable<ChatWithUser> group)
    {
        return group
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(item => item.Name)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault() ?? "unknown";
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

public class ChatWithUser
{
    public required string Text { get; set; }
    public required string Name { get; set; }
    public string? SteamId { get; set; }
    public long? SteamId64 { get; set; }
}
