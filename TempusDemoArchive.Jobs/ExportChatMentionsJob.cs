using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TempusDemoArchive.Persistence.Models.STVs;

namespace TempusDemoArchive.Jobs;

public class ExportChatMentionsJob : IJob
{
    private const string DefaultTerm = "hona";
    private const int DefaultContext = 50;
    private const long DefaultExcludeSteamId64 = 76561198221809496;

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"Search term (default: {DefaultTerm}):");
        var termInput = Console.ReadLine();
        var term = string.IsNullOrWhiteSpace(termInput) ? DefaultTerm : termInput.Trim();

        var context = ReadInt($"Context lines before/after (default: {DefaultContext}):", DefaultContext);

        var excludeSteamId64 = ReadSteamId64(
            $"Exclude SteamId64 (default: {DefaultExcludeSteamId64}, enter 'none' to skip):",
            DefaultExcludeSteamId64);

        Console.WriteLine("Filter map name (optional, exact match):");
        var mapFilter = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(mapFilter))
        {
            mapFilter = null;
        }

        var mentionRegex = BuildMentionRegex(term);

        await using var db = new ArchiveDbContext();

        IQueryable<StvChat> chatQuery = db.StvChats
            .AsNoTracking()
            .Where(chat => EF.Functions.Like(chat.Text, $"%{term}%"));

        if (mapFilter != null)
        {
            var mapDemoIds = db.Stvs
                .AsNoTracking()
                .Where(stv => stv.Header.Map == mapFilter)
                .Select(stv => stv.DemoId);
            chatQuery = chatQuery.Where(chat => mapDemoIds.Contains(chat.DemoId));
        }

        var candidates = await (from chat in chatQuery
            join user in db.StvUsers.AsNoTracking().Where(u => u.UserId != null)
                on new { chat.DemoId, UserId = chat.FromUserId!.Value }
                equals new { user.DemoId, UserId = user.UserId!.Value }
                into users
            from user in users.DefaultIfEmpty()
            select new ChatMatchCandidate
            {
                DemoId = chat.DemoId,
                Index = chat.Index,
                Tick = chat.Tick,
                Text = chat.Text,
                From = chat.From,
                SteamId64 = user != null ? user.SteamId64 : null
            }).ToListAsync(cancellationToken);

        var excludedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            term,
            "honajump"
        };

        var matches = candidates
            .Where(candidate => IsPlayerChatText(candidate.Text))
            .Where(candidate => mentionRegex.IsMatch(candidate.Text))
            .Where(candidate => excludeSteamId64 == null || candidate.SteamId64 != excludeSteamId64)
            .Where(candidate => !IsExcludedSpeaker(candidate.Text, excludedNames))
            .ToList();

        if (matches.Count == 0)
        {
            Console.WriteLine("No matches found.");
            return;
        }

        var demoIds = matches.Select(match => match.DemoId).Distinct().ToList();
        var demoDates = await db.Demos.AsNoTracking()
            .Where(demo => demoIds.Contains(demo.Id))
            .Select(demo => new { demo.Id, demo.Date })
            .ToDictionaryAsync(demo => demo.Id, demo => demo.Date, cancellationToken);
        var demoMaps = await db.Stvs.AsNoTracking()
            .Where(stv => demoIds.Contains(stv.DemoId))
            .Select(stv => new { stv.DemoId, Map = stv.Header.Map })
            .ToDictionaryAsync(stv => stv.DemoId, stv => stv.Map, cancellationToken);

        var contextByDemo = new Dictionary<ulong, ChatContextBlock>();
        foreach (var group in matches.GroupBy(match => match.DemoId))
        {
            var lines = await db.StvChats.AsNoTracking()
                .Where(chat => chat.DemoId == group.Key)
                .OrderBy(chat => chat.Index)
                .Select(chat => new ChatContextLine
                {
                    Index = chat.Index,
                    Tick = chat.Tick,
                    Text = chat.Text
                })
                .ToListAsync(cancellationToken);

            lines = lines.Where(line => IsPlayerChatText(line.Text)).ToList();

            var indexLookup = new Dictionary<int, int>(lines.Count);
            for (var i = 0; i < lines.Count; i++)
            {
                indexLookup[lines[i].Index] = i;
            }

            contextByDemo[group.Key] = new ChatContextBlock(lines, indexLookup);
        }

        var builder = new StringBuilder();
        builder.AppendLine($"Search term: {term}");
        builder.AppendLine($"Matches: {matches.Count}");
        builder.AppendLine($"Context: {context} lines");
        if (excludeSteamId64 != null)
        {
            builder.AppendLine($"Excluded SteamId64: {excludeSteamId64}");
        }
        if (mapFilter != null)
        {
            builder.AppendLine($"Map filter: {mapFilter}");
        }
        builder.AppendLine();

        var matchesByDemo = matches
            .GroupBy(match => match.DemoId)
            .OrderBy(group => demoDates.TryGetValue(group.Key, out var date) ? date : double.MaxValue)
            .ThenBy(group => group.Key)
            .ToList();

        foreach (var group in matchesByDemo)
        {
            var demoId = group.Key;
            var map = demoMaps.TryGetValue(demoId, out var mapName) ? mapName : "unknown";
            var dateText = demoDates.TryGetValue(demoId, out var date)
                ? ArchiveUtils.GetDateFromTimestamp(date).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                : "unknown";

            builder.AppendLine("----------------------------------------------------------------------------------------------------");
            builder.AppendLine($"Demo {demoId} | Map {map} | Date {dateText}");
            builder.AppendLine($"https://tempus2.xyz/demos/{demoId}");
            builder.AppendLine();

            if (!contextByDemo.TryGetValue(demoId, out var block))
            {
                builder.AppendLine("(No context lines loaded)");
                builder.AppendLine();
                continue;
            }

            var matchIndices = group.Select(match => match.Index).ToHashSet();
            var matchPositions = new List<int>();
            foreach (var matchIndex in matchIndices)
            {
                if (block.IndexLookup.TryGetValue(matchIndex, out var matchPosition))
                {
                    matchPositions.Add(matchPosition);
                }
            }

            if (matchPositions.Count == 0)
            {
                builder.AppendLine("(Match lines not found in context)");
                builder.AppendLine();
                continue;
            }

            matchPositions.Sort();
            var minPosition = matchPositions[0];
            var maxPosition = matchPositions[^1];
            var rangeStart = Math.Max(0, minPosition - context);
            var rangeEnd = Math.Min(block.Lines.Count - 1, maxPosition + context);

            for (var i = rangeStart; i <= rangeEnd; i++)
            {
                var line = block.Lines[i];
                var lineTick = line.Tick?.ToString(CultureInfo.InvariantCulture) ?? "-";
                var prefix = matchIndices.Contains(line.Index) ? ">>>" : "   ";
                builder.AppendLine($"{prefix} [{lineTick}] {line.Text}");
            }

            builder.AppendLine();
        }

        var fileName = ArchiveUtils.ToValidFileName($"chat_mentions_{term}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.txt");
        var filePath = Path.Combine(ArchivePath.TempRoot, fileName);
        await File.WriteAllTextAsync(filePath, builder.ToString(), cancellationToken);
        Console.WriteLine($"Wrote {filePath}");
    }

    private static int ReadInt(string prompt, int defaultValue)
    {
        Console.WriteLine(prompt);
        var input = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(input))
        {
            return defaultValue;
        }

        if (int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0)
        {
            return parsed;
        }

        return defaultValue;
    }

    private static long? ReadSteamId64(string prompt, long defaultValue)
    {
        Console.WriteLine(prompt);
        var input = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(input))
        {
            return defaultValue;
        }

        if (string.Equals(input, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(input, "skip", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return long.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
    }

    private static Regex BuildMentionRegex(string term)
    {
        var escaped = Regex.Escape(term);
        var pattern = $"(^|[^\\p{{L}}\\p{{N}}_]){escaped}(?:s|'s|’s|s'|s’)?(?=[^\\p{{L}}\\p{{N}}_]|$)";
        return new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }


    private static bool IsPlayerChatText(string text)
    {
        return text.Contains(" : ", StringComparison.Ordinal);
    }

    private static bool IsExcludedSpeaker(string text, IReadOnlySet<string> excludedNames)
    {
        var name = TryParseSpeakerName(text);
        return name != null && excludedNames.Contains(name);
    }

    private static string? TryParseSpeakerName(string text)
    {
        var marker = " : ";
        var markerIndex = text.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex <= 0)
        {
            return null;
        }

        var header = text.Substring(0, markerIndex).Trim();
        if (header.StartsWith("*", StringComparison.Ordinal))
        {
            header = header.TrimStart('*').Trim();
        }

        var bracketIndex = header.LastIndexOf(']');
        if (bracketIndex >= 0 && bracketIndex + 1 < header.Length)
        {
            header = header.Substring(bracketIndex + 1).Trim();
        }

        return string.IsNullOrWhiteSpace(header) ? null : header;
    }

    private sealed class ChatMatchCandidate
    {
        public ulong DemoId { get; init; }
        public int Index { get; init; }
        public int? Tick { get; init; }
        public string Text { get; init; } = string.Empty;
        public string From { get; init; } = string.Empty;
        public long? SteamId64 { get; init; }
    }

    private sealed class ChatContextLine
    {
        public int Index { get; init; }
        public int? Tick { get; init; }
        public string Text { get; init; } = string.Empty;
    }

    private sealed class ChatContextBlock
    {
        public ChatContextBlock(List<ChatContextLine> lines, Dictionary<int, int> indexLookup)
        {
            Lines = lines;
            IndexLookup = indexLookup;
        }

        public List<ChatContextLine> Lines { get; }
        public Dictionary<int, int> IndexLookup { get; }
    }

}
