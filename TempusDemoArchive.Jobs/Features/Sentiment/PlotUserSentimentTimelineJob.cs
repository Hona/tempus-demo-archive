using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using VaderSharp;

namespace TempusDemoArchive.Jobs;

public class PlotUserSentimentTimelineJob : IJob
{
    private const int DefaultWidth = 1200;
    private const int DefaultHeight = 600;
    private const int DefaultMargin = 70;

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine("Enter steam ID or Steam64:");
        var playerIdentifier = Console.ReadLine()?.Trim();

        if (string.IsNullOrWhiteSpace(playerIdentifier))
        {
            Console.WriteLine("No identifier provided.");
            return;
        }

        var bucket = GetBucket();
        Console.WriteLine($"Bucket: {bucket}");

        await using var db = new ArchiveDbContext();
        var analyzer = new SentimentIntensityAnalyzer();

        var query = ArchiveQueries.ChatsWithUsers(db)
            .Join(db.Demos.AsNoTracking(),
                chat => chat.DemoId,
                demo => demo.Id,
                (chat, demo) => new ChatWithDate
                {
                    DemoId = chat.DemoId,
                    Text = chat.Text,
                    Name = chat.Name,
                    SteamId = chat.SteamId,
                    SteamId64 = chat.SteamId64,
                    Timestamp = demo.Date
                });

        if (long.TryParse(playerIdentifier, out var steam64))
        {
            query = query.Where(message => message.SteamId64 == steam64);
        }
        else
        {
            query = query.Where(message => message.SteamId == playerIdentifier);
        }

        var buckets = new SortedDictionary<DateTime, SentimentAggregate>();
        var nameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var totalMessages = 0;

        await foreach (var message in query.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            var date = ArchiveUtils.GetDateFromTimestamp(message.Timestamp);
            var bucketDate = BucketDate(date, bucket);
            if (!buckets.TryGetValue(bucketDate, out var aggregate))
            {
                aggregate = new SentimentAggregate();
                buckets[bucketDate] = aggregate;
            }

            var body = ArchiveUtils.GetMessageBody(message.Text);
            aggregate.Sum += analyzer.PolarityScores(body).Compound;
            aggregate.Count++;
            totalMessages++;

            if (!string.IsNullOrWhiteSpace(message.Name))
            {
                nameCounts.TryGetValue(message.Name, out var count);
                nameCounts[message.Name] = count + 1;
            }
        }

        if (totalMessages == 0 || buckets.Count == 0)
        {
            Console.WriteLine("No messages found for that user.");
            return;
        }

        var displayName = nameCounts
            .OrderByDescending(entry => entry.Value)
            .Select(entry => entry.Key)
            .FirstOrDefault() ?? playerIdentifier;

        var points = buckets.Select(entry => new SentimentPoint(
            entry.Key,
            entry.Value.Sum / entry.Value.Count,
            entry.Value.Count)).ToList();

        var fileStem = ArchiveUtils.ToValidFileName($"sentiment_{playerIdentifier}_{bucket}");
        var csvPath = Path.Combine(ArchivePath.TempRoot, fileStem + ".csv");
        var svgPath = Path.Combine(ArchivePath.TempRoot, fileStem + ".svg");

        await WriteCsvAsync(csvPath, points, cancellationToken);
        WriteSvg(svgPath, points, displayName, bucket, totalMessages);

        Console.WriteLine($"Messages: {totalMessages:N0}");
        Console.WriteLine($"CSV: {csvPath}");
        Console.WriteLine($"SVG: {svgPath}");
    }

    private static string GetBucket()
    {
        var value = EnvVar.GetString("TEMPUS_SENTIMENT_BUCKET");
        if (string.Equals(value, "year", StringComparison.OrdinalIgnoreCase))
        {
            return "year";
        }

        return "month";
    }

    private static DateTime BucketDate(DateTime date, string bucket)
    {
        return string.Equals(bucket, "year", StringComparison.OrdinalIgnoreCase)
            ? new DateTime(date.Year, 1, 1)
            : new DateTime(date.Year, date.Month, 1);
    }

    private static async Task WriteCsvAsync(string path, IReadOnlyList<SentimentPoint> points,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.AppendLine("period_start,average_sentiment,message_count");
        foreach (var point in points)
        {
            builder.Append(point.Period.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(point.AverageSentiment.ToString("0.000", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(point.MessageCount.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine();
        }

        await File.WriteAllTextAsync(path, builder.ToString(), cancellationToken);
    }

    private static void WriteSvg(string path, IReadOnlyList<SentimentPoint> points, string displayName, string bucket,
        int totalMessages)
    {
        var width = DefaultWidth;
        var height = DefaultHeight;
        var margin = DefaultMargin;
        var plotWidth = width - margin * 2;
        var plotHeight = height - margin * 2;

        var yMin = -1.0;
        var yMax = 1.0;
        var xCount = Math.Max(points.Count - 1, 1);

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\">");
        sb.AppendLine("<rect width=\"100%\" height=\"100%\" fill=\"white\"/>");

        var title = $"Sentiment over time ({bucket}) - {Escape(displayName)}";
        sb.AppendLine($"<text x=\"{margin}\" y=\"{margin - 30}\" font-size=\"18\" font-family=\"sans-serif\">{title}</text>");
        sb.AppendLine($"<text x=\"{margin}\" y=\"{margin - 12}\" font-size=\"12\" font-family=\"sans-serif\">Messages: {totalMessages:N0}</text>");

        var left = margin;
        var right = width - margin;
        var bottom = height - margin;
        var top = margin;

        sb.AppendLine($"<line x1=\"{left}\" y1=\"{bottom}\" x2=\"{right}\" y2=\"{bottom}\" stroke=\"#333\" stroke-width=\"1\"/>");
        sb.AppendLine($"<line x1=\"{left}\" y1=\"{top}\" x2=\"{left}\" y2=\"{bottom}\" stroke=\"#333\" stroke-width=\"1\"/>");

        foreach (var yValue in new[] { -1.0, 0.0, 1.0 })
        {
            var y = MapY(yValue, top, plotHeight, yMin, yMax);
            var label = yValue.ToString("0.0", CultureInfo.InvariantCulture);
            sb.AppendLine($"<line x1=\"{left}\" y1=\"{y:0.##}\" x2=\"{right}\" y2=\"{y:0.##}\" stroke=\"#e0e0e0\" stroke-width=\"1\"/>");
            sb.AppendLine($"<text x=\"{left - 10}\" y=\"{y + 4:0.##}\" font-size=\"11\" font-family=\"sans-serif\" text-anchor=\"end\">{label}</text>");
        }

        var polylinePoints = new StringBuilder();
        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            var x = left + plotWidth * index / xCount;
            var y = MapY(point.AverageSentiment, top, plotHeight, yMin, yMax);
            if (polylinePoints.Length > 0)
            {
                polylinePoints.Append(' ');
            }

            polylinePoints.Append(CultureInfo.InvariantCulture, $"{x:0.##},{y:0.##}");
        }

        sb.AppendLine($"<polyline fill=\"none\" stroke=\"#2f4f4f\" stroke-width=\"2\" points=\"{polylinePoints}\"/>");

        var labelStride = Math.Max(1, points.Count / 8);
        for (var index = 0; index < points.Count; index += labelStride)
        {
            var point = points[index];
            var x = left + plotWidth * index / xCount;
            var label = point.Period.ToString(bucket == "year" ? "yyyy" : "yyyy-MM", CultureInfo.InvariantCulture);
            sb.AppendLine($"<text x=\"{x:0.##}\" y=\"{bottom + 18}\" font-size=\"11\" font-family=\"sans-serif\" text-anchor=\"middle\">{label}</text>");
        }

        sb.AppendLine("</svg>");
        File.WriteAllText(path, sb.ToString());
    }

    private static double MapY(double value, int top, int plotHeight, double min, double max)
    {
        var clamped = Math.Clamp(value, min, max);
        var ratio = (clamped - min) / (max - min);
        return top + plotHeight * (1 - ratio);
    }

    private static string Escape(string text)
    {
        return text.Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }

    private sealed class SentimentAggregate
    {
        public double Sum { get; set; }
        public int Count { get; set; }
    }

    private sealed class ChatWithDate
    {
        public ulong DemoId { get; init; }
        public required string Text { get; init; }
        public required string Name { get; init; }
        public string? SteamId { get; init; }
        public long? SteamId64 { get; init; }
        public double Timestamp { get; init; }
    }

    private sealed record SentimentPoint(DateTime Period, double AverageSentiment, int MessageCount);
}
