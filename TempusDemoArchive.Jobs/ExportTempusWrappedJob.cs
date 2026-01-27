using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VaderSharp;

namespace TempusDemoArchive.Jobs;

public class ExportTempusWrappedJob : IJob
{
    private static readonly Regex TokenRegex = new("[a-z0-9']+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> PlayableClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "soldier",
        "demoman"
    };

    private static readonly Regex DefaultMaskedWordRegex = new(
        @"(?i)(?<![\p{L}\p{N}_])(fuck[a-z]*|cunt[a-z]*|shit[a-z]*|bitch[a-z]*|retard[a-z]*)(?![\p{L}\p{N}_])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private enum MaskMode
    {
        None = 0,
        FirstLetter = 1,
        Full = 2
    }

    private const int ChatBuddyWindow = 3;

    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "as", "at", "be", "but", "by", "for", "from", "has", "have", "he", "her",
        "hers", "him", "his", "i", "im", "in", "is", "it", "its", "ive", "just", "like", "me", "my", "no",
        "not", "of", "oh", "ok", "okay", "on", "or", "our", "out", "so", "that", "the", "their", "them",
        "then", "there", "they", "this", "to", "u", "uh", "ur", "us", "was", "we", "were", "what", "when",
        "who", "why", "with", "ya", "ye", "you", "your", "yours", "rt", "gg", "wp"
    };

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var playerIdentifier = GetPlayerIdentifier();
        if (string.IsNullOrWhiteSpace(playerIdentifier))
        {
            Console.WriteLine("No identifier provided.");
            return;
        }

        var year = GetYear();
        var includeLogsInRaw = GetBoolEnv("TEMPUS_WRAPPED_INCLUDE_LOGS", defaultValue: false);
        var maskMode = GetMaskMode();
        var topWords = GetIntEnv("TEMPUS_WRAPPED_TOP_WORDS", defaultValue: 20, min: 0, max: 200);
        var topPhrases = GetIntEnv("TEMPUS_WRAPPED_TOP_PHRASES", defaultValue: 15, min: 0, max: 200);
        var topLists = GetIntEnv("TEMPUS_WRAPPED_TOP_LISTS", defaultValue: 10, min: 0, max: 200);

        var start = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var end = new DateTimeOffset(year + 1, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        await using var db = new ArchiveDbContext();

        var yearDemosQuery = db.Demos
            .AsNoTracking()
            .Where(demo => demo.Date >= start && demo.Date < end);

        var yearUsersQuery = ArchiveQueries.SteamUserQuery(db, playerIdentifier)
            .AsNoTracking()
            .Join(yearDemosQuery,
                user => user.DemoId,
                demo => demo.Id,
                (user, demo) => new TargetUserEntry
                {
                    DemoId = user.DemoId,
                    UserId = user.UserId!.Value,
                    Name = user.Name,
                    SteamId64 = user.SteamId64,
                    SteamId = user.SteamIdClean ?? user.SteamId,
                    DemoDate = demo.Date
                });

        var targetUsers = await yearUsersQuery.ToListAsync(cancellationToken);
        if (targetUsers.Count == 0)
        {
            Console.WriteLine("No demos found for that user/year.");
            return;
        }

        var demoDateById = targetUsers
            .GroupBy(entry => entry.DemoId)
            .ToDictionary(group => group.Key, group => group.First().DemoDate);
        var demoIds = demoDateById.Keys.ToList();

        var activityStartUtc = GetUtcDateFromTimestamp(demoDateById.Values.Min());
        var activityEndUtc = GetUtcDateFromTimestamp(demoDateById.Values.Max());

        var resolvedSteamId64 = targetUsers.Select(entry => entry.SteamId64).FirstOrDefault(id => id != null);
        var safeIdentifier = ArchiveUtils.ToValidFileName(playerIdentifier).Replace(' ', '_');

        var nameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in targetUsers)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                continue;
            }

            nameCounts.TryGetValue(entry.Name, out var current);
            nameCounts[entry.Name] = current + 1;
        }

        var displayName = nameCounts
            .OrderByDescending(entry => entry.Value)
            .Select(entry => entry.Key)
            .FirstOrDefault() ?? playerIdentifier;

        var safeName = ArchiveUtils.ToValidFileName(displayName).Replace(' ', '_');
        var idPart = resolvedSteamId64?.ToString(CultureInfo.InvariantCulture) ?? safeIdentifier;
        var finalStem = ArchiveUtils.ToValidFileName($"tempus_wrapped_{year}_{idPart}_{safeName}");

        var chatCsvPath = Path.Combine(ArchivePath.TempRoot, finalStem + "_chat.csv");
        var chatTxtPath = Path.Combine(ArchivePath.TempRoot, finalStem + "_chat.txt");
        var rawPath = Path.Combine(ArchivePath.TempRoot, finalStem + "_raw.txt");

        var demoMetaById = await LoadDemoMetaAsync(db, demoIds, cancellationToken);

        var demoUserIds = BuildDemoUserIds(targetUsers);

        var playtime = await ComputePlaytimeAsync(db, demoIds, demoUserIds, demoMetaById, demoDateById,
            yearUsersQuery, cancellationToken);

        var chatQuery =
            from chat in db.StvChats.AsNoTracking()
            join user in yearUsersQuery
                on new { chat.DemoId, UserId = chat.FromUserId }
                equals new { user.DemoId, UserId = (int?)user.UserId }
            orderby user.DemoDate, chat.DemoId, chat.Index
            select new ChatEventRow
            {
                Timestamp = user.DemoDate,
                DemoId = chat.DemoId,
                ChatIndex = chat.Index,
                Tick = chat.Tick,
                Text = chat.Text
            };

        var totalMessages = 0;
        DateTime? firstMessageUtc = null;
        DateTime? lastMessageUtc = null;
        var mapCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var serverCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var monthCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var dayCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var hourCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var dowCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        var topPositive = new List<ScoredChat>(5);
        var topNegative = new List<ScoredChat>(5);
        ScoredChat? funniest = null;
        ScoredChat? rudest = null;

        var lengthSamples = new List<int>();
        long totalBodyChars = 0;
        var exclamationTotal = 0;
        var questionTotal = 0;
        var laughterMessages = 0;
        var thanksMessages = 0;
        var sorryMessages = 0;

        var wordCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var phraseCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var sentimentAnalyzer = new SentimentIntensityAnalyzer();
        double sentimentSum = 0;
        var sentimentCount = 0;
        var sentimentByMonth = new Dictionary<string, SentimentAggregate>(StringComparer.Ordinal);

        await using (var csvStream = File.Open(chatCsvPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        await using (var csvWriter = new StreamWriter(csvStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        await using (var txtStream = File.Open(chatTxtPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        await using (var txtWriter = new StreamWriter(txtStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            WriteCsvRow(csvWriter, "timestamp_utc", "map", "server", "demo_id", "tick", "chat_index", "text");

            await foreach (var row in chatQuery.AsAsyncEnumerable().WithCancellation(cancellationToken))
            {
                totalMessages++;

                var dtUtc = GetUtcDateFromTimestamp(row.Timestamp);
                firstMessageUtc ??= dtUtc;
                lastMessageUtc = dtUtc;

                var map = demoMetaById.TryGetValue(row.DemoId, out var meta) ? meta.Map : "unknown";
                var server = demoMetaById.TryGetValue(row.DemoId, out meta) ? meta.Server : "unknown";

                mapCounts.TryGetValue(map, out var mapCurrent);
                mapCounts[map] = mapCurrent + 1;

                serverCounts.TryGetValue(server, out var serverCurrent);
                serverCounts[server] = serverCurrent + 1;

                var monthKey = dtUtc.ToString("yyyy-MM", CultureInfo.InvariantCulture);
                monthCounts.TryGetValue(monthKey, out var monthCurrent);
                monthCounts[monthKey] = monthCurrent + 1;

                var dayKey = dtUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                dayCounts.TryGetValue(dayKey, out var dayCurrent);
                dayCounts[dayKey] = dayCurrent + 1;

                var hourKey = dtUtc.ToString("HH", CultureInfo.InvariantCulture);
                hourCounts.TryGetValue(hourKey, out var hourCurrent);
                hourCounts[hourKey] = hourCurrent + 1;

                var dowKey = dtUtc.ToString("ddd", CultureInfo.InvariantCulture);
                dowCounts.TryGetValue(dowKey, out var dowCurrent);
                dowCounts[dowKey] = dowCurrent + 1;

                var body = ArchiveUtils.GetMessageBody(row.Text);
                totalBodyChars += body.Length;
                lengthSamples.Add(body.Length);
                exclamationTotal += CountChar(body, '!');
                questionTotal += CountChar(body, '?');

                var bodyLower = body.ToLowerInvariant();
                var tokens = Tokenize(bodyLower);
                var tokenSet = tokens.Count == 0 ? null : new HashSet<string>(tokens, StringComparer.OrdinalIgnoreCase);

                if (tokenSet != null)
                {
                    if (tokenSet.Contains("lol") || tokenSet.Contains("lmao") || tokenSet.Contains("rofl") || tokenSet.Contains("haha"))
                    {
                        laughterMessages++;
                    }

                    if (tokenSet.Contains("ty") || tokenSet.Contains("thanks") || tokenSet.Contains("thx"))
                    {
                        thanksMessages++;
                    }

                    if (tokenSet.Contains("sorry") || tokenSet.Contains("sry"))
                    {
                        sorryMessages++;
                    }
                }

                foreach (var token in tokens)
                {
                    if (Stopwords.Contains(token))
                    {
                        continue;
                    }

                    wordCounts.TryGetValue(token, out var current);
                    wordCounts[token] = current + 1;
                }

                for (var i = 0; i + 1 < tokens.Count; i++)
                {
                    var a = tokens[i];
                    var b = tokens[i + 1];
                    if (Stopwords.Contains(a) || Stopwords.Contains(b))
                    {
                        continue;
                    }

                    var phrase = a + " " + b;
                    phraseCounts.TryGetValue(phrase, out var current);
                    phraseCounts[phrase] = current + 1;
                }

                var sentiment = sentimentAnalyzer.PolarityScores(body).Compound;
                sentimentSum += sentiment;
                sentimentCount++;
                if (!sentimentByMonth.TryGetValue(monthKey, out var agg))
                {
                    agg = new SentimentAggregate();
                    sentimentByMonth[monthKey] = agg;
                }

                agg.Sum += sentiment;
                agg.Count++;

                var scored = new ScoredChat(dtUtc, row.DemoId, map, server, row.Text, body, sentiment);
                TrackTop(topPositive, scored, highestFirst: true, max: 5);
                TrackTop(topNegative, scored, highestFirst: false, max: 5);

                if (rudest == null || scored.Sentiment < rudest.Sentiment)
                {
                    rudest = scored;
                }

                if (tokenSet != null && ContainsLaughter(tokenSet))
                {
                    if (funniest == null || scored.Sentiment > funniest.Sentiment)
                    {
                        funniest = scored;
                    }
                }

                WriteCsvRow(csvWriter,
                    dtUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    map,
                    server,
                    row.DemoId.ToString(CultureInfo.InvariantCulture),
                    row.Tick?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    row.ChatIndex.ToString(CultureInfo.InvariantCulture),
                    MaskForDisplay(row.Text, maskMode));

                await txtWriter.WriteAsync(dtUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                await txtWriter.WriteAsync(" | ");
                await txtWriter.WriteAsync(map);
                await txtWriter.WriteAsync(" | ");
                await txtWriter.WriteAsync(server);
                await txtWriter.WriteAsync(" | ");
                await txtWriter.WriteAsync(MaskForDisplay(row.Text, maskMode));
                await txtWriter.WriteLineAsync();
            }
        }

        var windowStartUtc = firstMessageUtc ?? activityStartUtc;
        var windowEndUtc = lastMessageUtc ?? activityEndUtc;

        lengthSamples.Sort();
        var avgLength = totalMessages == 0 ? 0 : (double)totalBodyChars / totalMessages;
        var medianLength = GetMedian(lengthSamples);

        KeyValuePair<string, int>? peakDay = dayCounts.Count == 0
            ? null
            : dayCounts.OrderByDescending(entry => entry.Value).First();
        KeyValuePair<string, int>? peakHour = hourCounts.Count == 0
            ? null
            : hourCounts.OrderByDescending(entry => entry.Value).First();
        KeyValuePair<string, int>? peakMap = mapCounts.Count == 0
            ? null
            : mapCounts.OrderByDescending(entry => entry.Value).First();
        KeyValuePair<string, int>? peakServer = serverCounts.Count == 0
            ? null
            : serverCounts.OrderByDescending(entry => entry.Value).First();

        var overallSentiment = sentimentCount == 0 ? 0 : sentimentSum / sentimentCount;

        funniest ??= topPositive.FirstOrDefault();

        var includeInferredWr = GetBoolEnv("TEMPUS_WRAPPED_INCLUDE_INFERRED_WR", defaultValue: false);
        var includeLookupWr = GetBoolEnv("TEMPUS_WRAPPED_INCLUDE_LOOKUP_WR", defaultValue: false);

        var resolvedSteamId = targetUsers
            .Select(entry => entry.SteamId)
            .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id)) ?? string.Empty;

        var friends = await ComputeFriendsAsync(db, demoIds, demoUserIds, playtime.ByDemoSeconds, resolvedSteamId64,
            resolvedSteamId, cancellationToken);

        var targetNamesByDemoId = BuildNamesByDemo(targetUsers);
        var wrEventsRaw = await ComputeWrEventsAsync(db, demoIds, demoMetaById, demoDateById, targetNamesByDemoId,
            cancellationToken);

        var inferredWrCount = DedupWrImprovements(wrEventsRaw.Where(wr => wr.Inferred)).Count;
        var lookupWrCount = DedupWrImprovements(wrEventsRaw.Where(IsLookupWr)).Count;

        var wrEvents = DedupWrImprovements(wrEventsRaw.Where(wr =>
            (includeInferredWr || !wr.Inferred)
            && (includeLookupWr || !IsLookupWr(wr))));

        KeyValuePair<string, double>? peakPlaytimeDow = playtime.ByDowSeconds.Count == 0
            ? null
            : playtime.ByDowSeconds.OrderByDescending(entry => entry.Value).First();
        KeyValuePair<string, double>? peakPlaytimeDay = playtime.ByDaySeconds.Count == 0
            ? null
            : playtime.ByDaySeconds.OrderByDescending(entry => entry.Value).First();
        KeyValuePair<string, double>? peakPlaytimeMap = playtime.ByMapSeconds.Count == 0
            ? null
            : playtime.ByMapSeconds.OrderByDescending(entry => entry.Value).First();
        KeyValuePair<string, double>? peakPlaytimeServer = playtime.ByServerSeconds.Count == 0
            ? null
            : playtime.ByServerSeconds.OrderByDescending(entry => entry.Value).First();

        var favoriteClass = playtime.SoldierSeconds >= playtime.DemoSeconds ? "Soldier" : "Demoman";

        EnsureDefaultSystemPrompt();

        await using (var rawStream = File.Open(rawPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        await using (var rawWriter = new StreamWriter(rawStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            await rawWriter.WriteLineAsync($"Tempus Wrapped Raw ({year})");
            await rawWriter.WriteLineAsync($"Player: {displayName}");
            await rawWriter.WriteLineAsync($"Identifier: {playerIdentifier}");
            if (resolvedSteamId64 != null)
            {
                await rawWriter.WriteLineAsync($"SteamId64: {resolvedSteamId64}");
            }

            await rawWriter.WriteLineAsync("Timezone: UTC");
            await rawWriter.WriteLineAsync($"Activity window: {activityStartUtc:yyyy-MM-dd} -> {activityEndUtc:yyyy-MM-dd} (UTC)");
            await rawWriter.WriteLineAsync($"Chat window: {windowStartUtc:yyyy-MM-dd} -> {windowEndUtc:yyyy-MM-dd} (UTC)");
            await rawWriter.WriteLineAsync();

            await rawWriter.WriteLineAsync("COUNTS");
            await rawWriter.WriteLineAsync($"demos: {demoIds.Count}");
            await rawWriter.WriteLineAsync($"demos_with_playtime: {playtime.DemosWithTime}");
            await rawWriter.WriteLineAsync($"chat_messages: {totalMessages}");
            await rawWriter.WriteLineAsync($"playtime_total: {FormatHours(playtime.TotalSeconds)}");
            await rawWriter.WriteLineAsync($"playtime_soldier: {FormatHours(playtime.SoldierSeconds)}");
            await rawWriter.WriteLineAsync($"playtime_demoman: {FormatHours(playtime.DemoSeconds)}");
            await rawWriter.WriteLineAsync($"playtime_maps: {playtime.ByMapSeconds.Count}");
            await rawWriter.WriteLineAsync($"playtime_servers: {playtime.ByServerSeconds.Count}");
            await rawWriter.WriteLineAsync();

            await rawWriter.WriteLineAsync("PLAYTIME (soldier/demoman alive time)");
            await rawWriter.WriteLineAsync($"favorite_class: {favoriteClass}");
            await rawWriter.WriteLineAsync($"most_active_dow_utc: {(peakPlaytimeDow.HasValue ? $"{peakPlaytimeDow.Value.Key} ({FormatHours(peakPlaytimeDow.Value.Value)})" : "n/a")}");
            await rawWriter.WriteLineAsync($"most_active_day_utc: {(peakPlaytimeDay.HasValue ? $"{peakPlaytimeDay.Value.Key} ({FormatHours(peakPlaytimeDay.Value.Value)})" : "n/a")}");
            await rawWriter.WriteLineAsync($"favorite_map_by_playtime: {(peakPlaytimeMap.HasValue ? $"{peakPlaytimeMap.Value.Key} ({FormatHours(peakPlaytimeMap.Value.Value)})" : "n/a")}");
            await rawWriter.WriteLineAsync($"favorite_server_by_playtime: {(peakPlaytimeServer.HasValue ? $"{peakPlaytimeServer.Value.Key} ({FormatHours(peakPlaytimeServer.Value.Value)})" : "n/a")}");
            await rawWriter.WriteLineAsync();

            await rawWriter.WriteLineAsync("PLAYTIME_BY_DAY_OF_WEEK_UTC");
            foreach (var day in new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" })
            {
                if (playtime.ByDowSeconds.TryGetValue(day, out var seconds))
                {
                    await rawWriter.WriteLineAsync($"{day}: {FormatHours(seconds)}");
                }
            }

            await rawWriter.WriteLineAsync();
            await rawWriter.WriteLineAsync("PLAYTIME_BY_MONTH");
            foreach (var entry in playtime.ByMonthSeconds.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                await rawWriter.WriteLineAsync($"{entry.Key}: {FormatHours(entry.Value)}");
            }

            await rawWriter.WriteLineAsync();
            await rawWriter.WriteLineAsync("TOP_MAPS_BY_PLAYTIME");
            foreach (var entry in playtime.ByMapSeconds.OrderByDescending(entry => entry.Value).Take(topLists))
            {
                await rawWriter.WriteLineAsync($"{entry.Key}: {FormatHours(entry.Value)}");
            }

            await rawWriter.WriteLineAsync();
            await rawWriter.WriteLineAsync("TOP_SERVERS_BY_PLAYTIME");
            foreach (var entry in playtime.ByServerSeconds.OrderByDescending(entry => entry.Value).Take(topLists))
            {
                await rawWriter.WriteLineAsync($"{entry.Key}: {FormatHours(entry.Value)}");
            }

            await rawWriter.WriteLineAsync();

            await rawWriter.WriteLineAsync("CHAT_PEAKS");
            await rawWriter.WriteLineAsync($"peak_day_utc: {(peakDay.HasValue ? $"{peakDay.Value.Key} ({peakDay.Value.Value})" : "n/a")}");
            await rawWriter.WriteLineAsync($"peak_hour_utc: {(peakHour.HasValue ? $"{peakHour.Value.Key}:00 ({peakHour.Value.Value})" : "n/a")}");
            await rawWriter.WriteLineAsync($"top_map_by_messages: {(peakMap.HasValue ? $"{peakMap.Value.Key} ({peakMap.Value.Value})" : "n/a")}");
            await rawWriter.WriteLineAsync($"top_server_by_messages: {(peakServer.HasValue ? $"{peakServer.Value.Key} ({peakServer.Value.Value})" : "n/a")}");
            await rawWriter.WriteLineAsync();

            await rawWriter.WriteLineAsync("SENTIMENT (VADER compound)");
            await rawWriter.WriteLineAsync($"average: {overallSentiment:0.000}");
            await rawWriter.WriteLineAsync();

            await rawWriter.WriteLineAsync("CHAT_MESSAGES_BY_MONTH");
            foreach (var entry in monthCounts.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                await rawWriter.WriteLineAsync($"{entry.Key}: {entry.Value}");
            }

            await rawWriter.WriteLineAsync();
            await rawWriter.WriteLineAsync("SENTIMENT_BY_MONTH (VADER compound)");
            foreach (var entry in sentimentByMonth.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                var average = entry.Value.Count == 0 ? 0 : entry.Value.Sum / entry.Value.Count;
                await rawWriter.WriteLineAsync($"{entry.Key}: {average:0.000} ({entry.Value.Count})");
            }

            await rawWriter.WriteLineAsync();
            await rawWriter.WriteLineAsync("CHAT_MESSAGES_BY_DAY_OF_WEEK_UTC");
            foreach (var day in new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" })
            {
                if (dowCounts.TryGetValue(day, out var count))
                {
                    await rawWriter.WriteLineAsync($"{day}: {count}");
                }
            }

            await rawWriter.WriteLineAsync();
            await rawWriter.WriteLineAsync("TOP_CHAT_DAYS");
            foreach (var entry in dayCounts.OrderByDescending(entry => entry.Value).Take(topLists))
            {
                await rawWriter.WriteLineAsync($"{entry.Key}: {entry.Value}");
            }

            await rawWriter.WriteLineAsync();
            await rawWriter.WriteLineAsync("TOP_CHAT_HOURS_UTC");
            foreach (var entry in hourCounts.OrderByDescending(entry => entry.Value).Take(topLists))
            {
                await rawWriter.WriteLineAsync($"{entry.Key}:00: {entry.Value}");
            }

            await rawWriter.WriteLineAsync();
            await rawWriter.WriteLineAsync("TOP_MAPS_BY_MESSAGES");
            foreach (var entry in mapCounts.OrderByDescending(entry => entry.Value).Take(topLists))
            {
                await rawWriter.WriteLineAsync($"{entry.Key}: {entry.Value}");
            }

            await rawWriter.WriteLineAsync();
            await rawWriter.WriteLineAsync("TOP_SERVERS_BY_MESSAGES");
            foreach (var entry in serverCounts.OrderByDescending(entry => entry.Value).Take(topLists))
            {
                await rawWriter.WriteLineAsync($"{entry.Key}: {entry.Value}");
            }

            await rawWriter.WriteLineAsync();
            await rawWriter.WriteLineAsync("STYLE");
            await rawWriter.WriteLineAsync($"avg_message_length_chars: {avgLength:0.0}");
            await rawWriter.WriteLineAsync($"median_message_length_chars: {medianLength:0}");
            await rawWriter.WriteLineAsync($"exclamation_total: {exclamationTotal}");
            await rawWriter.WriteLineAsync($"question_total: {questionTotal}");
            await rawWriter.WriteLineAsync($"laughter_messages: {laughterMessages}");
            await rawWriter.WriteLineAsync($"thanks_messages: {thanksMessages}");
            await rawWriter.WriteLineAsync($"sorry_messages: {sorryMessages}");

            await rawWriter.WriteLineAsync();
            await rawWriter.WriteLineAsync("MESSAGE_HIGHLIGHTS");
            await rawWriter.WriteLineAsync(funniest != null
                ? $"funniest_candidate: {funniest.Sentiment:0.000} | {funniest.TimestampUtc:yyyy-MM-dd} | {funniest.Map} | {MaskForDisplay(funniest.Body, maskMode)}"
                : "funniest_candidate: n/a");
            await rawWriter.WriteLineAsync(rudest != null
                ? $"rudest_candidate: {rudest.Sentiment:0.000} | {rudest.TimestampUtc:yyyy-MM-dd} | {rudest.Map} | {MaskForDisplay(rudest.Body, maskMode)}"
                : "rudest_candidate: n/a");

            if (topPositive.Count > 0)
            {
                await rawWriter.WriteLineAsync();
                await rawWriter.WriteLineAsync("TOP_POSITIVE_MESSAGES");
                foreach (var msg in topPositive)
                {
                    await rawWriter.WriteLineAsync(
                        $"{msg.Sentiment:0.000} | {msg.TimestampUtc:yyyy-MM-dd} | {msg.Map} | {MaskForDisplay(msg.Body, maskMode)}");
                }
            }

            if (topNegative.Count > 0)
            {
                await rawWriter.WriteLineAsync();
                await rawWriter.WriteLineAsync("TOP_NEGATIVE_MESSAGES");
                foreach (var msg in topNegative)
                {
                    await rawWriter.WriteLineAsync(
                        $"{msg.Sentiment:0.000} | {msg.TimestampUtc:yyyy-MM-dd} | {msg.Map} | {MaskForDisplay(msg.Body, maskMode)}");
                }
            }

            if (topWords > 0)
            {
                await rawWriter.WriteLineAsync();
                await rawWriter.WriteLineAsync("TOP_WORDS");
                foreach (var entry in wordCounts.OrderByDescending(entry => entry.Value).Take(topWords))
                {
                    await rawWriter.WriteLineAsync($"{MaskForDisplay(entry.Key, maskMode)}: {entry.Value}");
                }
            }

            if (topPhrases > 0)
            {
                await rawWriter.WriteLineAsync();
                await rawWriter.WriteLineAsync("TOP_PHRASES (bigrams)");
                foreach (var entry in phraseCounts.OrderByDescending(entry => entry.Value).Take(topPhrases))
                {
                    await rawWriter.WriteLineAsync($"{MaskForDisplay(entry.Key, maskMode)}: {entry.Value}");
                }
            }

            if (friends.Count > 0)
            {
                await rawWriter.WriteLineAsync();
                await rawWriter.WriteLineAsync("FRIENDS_BY_SHARED_PLAYTIME");
                foreach (var friend in friends.Take(topLists))
                {
                    await rawWriter.WriteLineAsync(
                        $"{friend.Name}: {FormatHours(friend.SharedPlaytimeSeconds)} | demos {friend.SharedDemos} | chat {friend.ChatMessages} | replies_after_you {friend.RepliesAfterYou} | replies_before_you {friend.RepliesBeforeYou}");
                }

                await rawWriter.WriteLineAsync();
                await rawWriter.WriteLineAsync("FRIENDS_BY_SHARED_DEMOS");
                foreach (var friend in friends.OrderByDescending(f => f.SharedDemos).ThenByDescending(f => f.SharedPlaytimeSeconds)
                             .Take(topLists))
                {
                    await rawWriter.WriteLineAsync(
                        $"{friend.Name}: demos {friend.SharedDemos} | {FormatHours(friend.SharedPlaytimeSeconds)}");
                }

                await rawWriter.WriteLineAsync();
                await rawWriter.WriteLineAsync("CHAT_BUDDIES (reply proximity)");
                foreach (var friend in friends
                             .OrderByDescending(f => f.RepliesAfterYou + f.RepliesBeforeYou)
                             .ThenByDescending(f => f.ChatMessages)
                             .Take(topLists))
                {
                    var totalReplies = friend.RepliesAfterYou + friend.RepliesBeforeYou;
                    await rawWriter.WriteLineAsync(
                        $"{friend.Name}: interactions {totalReplies} | after_you {friend.RepliesAfterYou} | before_you {friend.RepliesBeforeYou} | chat {friend.ChatMessages}");
                }
            }

            await rawWriter.WriteLineAsync();
            await rawWriter.WriteLineAsync("WORLD_RECORDS (WRs)");
            await rawWriter.WriteLineAsync($"include_inferred: {includeInferredWr}");
            await rawWriter.WriteLineAsync($"include_lookup_sources: {includeLookupWr}");
            await rawWriter.WriteLineAsync($"count: {wrEvents.Count}");
            if (!includeInferredWr)
            {
                await rawWriter.WriteLineAsync($"excluded_inferred: {inferredWrCount}");
            }
            if (!includeLookupWr)
            {
                await rawWriter.WriteLineAsync($"excluded_lookup_sources: {lookupWrCount}");
            }
            foreach (var wr in wrEvents)
            {
                await rawWriter.WriteLineAsync(
                    $"{wr.TimestampUtc:yyyy-MM-dd} | {wr.Class} | {wr.Map} | {wr.Source} | {wr.RecordTime} | demo {wr.DemoId} | inferred {wr.Inferred}");
            }

            await rawWriter.WriteLineAsync();
            await rawWriter.WriteLineAsync("OUTPUT_FILES");
            await rawWriter.WriteLineAsync($"chat_csv: {chatCsvPath}");
            await rawWriter.WriteLineAsync($"chat_txt: {chatTxtPath}");
            await rawWriter.WriteLineAsync($"system_prompt: {GetSystemPromptPath()}");

            if (includeLogsInRaw)
            {
                await rawWriter.WriteLineAsync();
                await rawWriter.WriteLineAsync("CHAT_LOGS (chronological, UTC)");
                await rawWriter.WriteLineAsync("format: timestamp | map | server | raw_text");
                await rawWriter.WriteLineAsync();

                await using var chatStream = File.OpenRead(chatTxtPath);
                using var chatReader = new StreamReader(chatStream, Encoding.UTF8);
                while (await chatReader.ReadLineAsync(cancellationToken) is { } line)
                {
                    await rawWriter.WriteLineAsync(line);
                }
            }
        }

        Console.WriteLine($"Chat messages: {totalMessages:N0}");
        Console.WriteLine($"Playtime: {FormatHours(playtime.TotalSeconds)} (soldier {FormatHours(playtime.SoldierSeconds)}, demo {FormatHours(playtime.DemoSeconds)})");
        var wrMode = includeInferredWr ? "including inferred" : "excluding inferred";
        if (!includeLookupWr)
        {
            wrMode += ", excluding lookup";
        }

        Console.WriteLine($"WRs: {wrEvents.Count:N0} ({wrMode})");
        Console.WriteLine($"Raw: {rawPath}");
        Console.WriteLine($"Chat CSV: {chatCsvPath}");
        Console.WriteLine($"Chat TXT: {chatTxtPath}");
        Console.WriteLine($"System prompt: {GetSystemPromptPath()}");
    }

    private static string? GetPlayerIdentifier()
    {
        var env = Environment.GetEnvironmentVariable("TEMPUS_WRAPPED_USER");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env.Trim();
        }

        Console.WriteLine("Enter steam ID or Steam64:");
        return Console.ReadLine()?.Trim();
    }

    private static int GetYear()
    {
        var defaultYear = DateTimeOffset.UtcNow.Year - 1;

        var env = Environment.GetEnvironmentVariable("TEMPUS_WRAPPED_YEAR");
        if (int.TryParse(env, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        Console.WriteLine($"Year (default: {defaultYear}):");
        var input = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(input))
        {
            return defaultYear;
        }

        return int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
            ? parsed
            : defaultYear;
    }

    private static bool GetBoolEnv(string name, bool defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(value, "0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return defaultValue;
    }

    private static int GetIntEnv(string name, int defaultValue, int min, int max)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return defaultValue;
        }

        return Math.Clamp(parsed, min, max);
    }

    private static MaskMode GetMaskMode()
    {
        var value = Environment.GetEnvironmentVariable("TEMPUS_WRAPPED_MASK_MODE");
        if (string.IsNullOrWhiteSpace(value))
        {
            return MaskMode.FirstLetter;
        }

        value = value.Trim();

        if (string.Equals(value, "0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "off", StringComparison.OrdinalIgnoreCase))
        {
            return MaskMode.None;
        }

        if (string.Equals(value, "full", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "redact", StringComparison.OrdinalIgnoreCase))
        {
            return MaskMode.Full;
        }

        return MaskMode.FirstLetter;
    }

    private static string MaskForDisplay(string text, MaskMode mode)
    {
        if (mode == MaskMode.None || string.IsNullOrEmpty(text))
        {
            return text;
        }

        return DefaultMaskedWordRegex.Replace(text, match => MaskWord(match.Value, mode));
    }

    private static string MaskWord(string word, MaskMode mode)
    {
        if (string.IsNullOrEmpty(word))
        {
            return word;
        }

        if (mode == MaskMode.Full)
        {
            return new string('*', word.Length);
        }

        if (word.Length == 1)
        {
            return word;
        }

        return word[0] + new string('*', word.Length - 1);
    }

    private static DateTime GetUtcDateFromTimestamp(double timestampSeconds)
    {
        var milliseconds = (long)(timestampSeconds * 1000);
        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;
    }

    private static int CountChar(string value, char ch)
    {
        var count = 0;
        foreach (var c in value)
        {
            if (c == ch)
            {
                count++;
            }
        }

        return count;
    }

    private static List<string> Tokenize(string lower)
    {
        var matches = TokenRegex.Matches(lower);
        if (matches.Count == 0)
        {
            return new List<string>();
        }

        var tokens = new List<string>(matches.Count);
        foreach (Match match in matches)
        {
            var token = match.Value;
            if (token.Length < 2)
            {
                continue;
            }

            tokens.Add(token);
        }

        return tokens;
    }

    private static double GetMedian(List<int> sorted)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        var mid = sorted.Count / 2;
        if (sorted.Count % 2 == 1)
        {
            return sorted[mid];
        }

        return (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    private static void WriteCsvRow(TextWriter writer, params string[] fields)
    {
        for (var i = 0; i < fields.Length; i++)
        {
            if (i > 0)
            {
                writer.Write(',');
            }

            writer.Write(EscapeCsv(fields[i]));
        }

        writer.WriteLine();
    }

    private static string EscapeCsv(string value)
    {
        var needsQuotes = value.Contains(',', StringComparison.Ordinal)
                          || value.Contains('"', StringComparison.Ordinal)
                          || value.Contains('\n', StringComparison.Ordinal)
                          || value.Contains('\r', StringComparison.Ordinal);
        if (!needsQuotes)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string MoveToStem(string currentPath, string fileName)
    {
        var nextPath = Path.Combine(ArchivePath.TempRoot, fileName);
        if (string.Equals(currentPath, nextPath, StringComparison.OrdinalIgnoreCase))
        {
            return currentPath;
        }

        if (File.Exists(nextPath))
        {
            return currentPath;
        }

        File.Move(currentPath, nextPath);
        return nextPath;
    }

    private static string GetSystemPromptPath()
    {
        return Path.Combine(ArchivePath.Root, "prompts", "tempus_wrapped_system_prompt.txt");
    }

    private static void EnsureDefaultSystemPrompt()
    {
        var path = GetSystemPromptPath();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (File.Exists(path))
        {
            return;
        }

        var prompt = "You are writing a fun 'Tempus Wrapped' recap for a TF2 jump community player.\n"
                     + "You will receive a raw data dump containing chat stats, playtime (soldier/demoman alive time), top maps/servers by playtime, friends/peers summaries, word/phrase frequencies, WR events, and optionally full chat logs.\n\n"
                     + "Rules:\n"
                     + "- Use concrete numbers from the data.\n"
                     + "- Keep it playful but respectful; don't dunk on the player or others.\n"
                     + "- Do not repeat slurs/offensive language; if present in logs, paraphrase and avoid quoting.\n"
                     + "- Do not include Steam IDs unless explicitly asked.\n"
                     + "- If data looks sparse, call it out lightly and keep it short.\n\n"
                     + "Output:\n"
                     + "- Plain text only.\n"
                     + "- Title line: 'Tempus Wrapped <year> — <player>'\n"
                     + "- 5-7 short sections (Highlights, When They Played, Top Maps, Favorite Class, Crew/Friends, Chat Style, WRs).\n"
                     + "- Keep it under ~250 words unless asked otherwise.\n";

        File.WriteAllText(path, prompt);
    }

    private static bool ContainsLaughter(IReadOnlySet<string> tokens)
    {
        return tokens.Contains("lol")
               || tokens.Contains("lmao")
               || tokens.Contains("rofl")
               || tokens.Contains("haha");
    }

    private static void TrackTop(List<ScoredChat> list, ScoredChat candidate, bool highestFirst, int max)
    {
        list.Add(candidate);

        list.Sort((left, right) =>
        {
            var sentimentCompare = highestFirst
                ? right.Sentiment.CompareTo(left.Sentiment)
                : left.Sentiment.CompareTo(right.Sentiment);
            if (sentimentCompare != 0)
            {
                return sentimentCompare;
            }

            // Prefer longer messages when sentiment ties
            return right.Body.Length.CompareTo(left.Body.Length);
        });

        if (list.Count > max)
        {
            list.RemoveRange(max, list.Count - max);
        }
    }

    private static Dictionary<ulong, HashSet<int>> BuildDemoUserIds(IEnumerable<TargetUserEntry> targetUsers)
    {
        var demoUserIds = new Dictionary<ulong, HashSet<int>>();
        foreach (var entry in targetUsers)
        {
            if (!demoUserIds.TryGetValue(entry.DemoId, out var ids))
            {
                ids = new HashSet<int>();
                demoUserIds[entry.DemoId] = ids;
            }

            ids.Add(entry.UserId);
        }

        return demoUserIds;
    }

    private static async Task<Dictionary<ulong, DemoMeta>> LoadDemoMetaAsync(ArchiveDbContext db,
        IReadOnlyList<ulong> demoIds, CancellationToken cancellationToken)
    {
        var metas = new Dictionary<ulong, DemoMeta>();
        foreach (var chunk in ChunkIds(demoIds))
        {
            var rows = await db.Stvs
                .AsNoTracking()
                .Where(stv => chunk.Contains(stv.DemoId))
                .Select(stv => new DemoMeta(stv.DemoId, stv.Header.Map, stv.Header.Server, stv.IntervalPerTick,
                    stv.Header.Ticks))
                .ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                metas[row.DemoId] = row;
            }
        }

        return metas;
    }

    private static async Task<PlaytimeResult> ComputePlaytimeAsync(ArchiveDbContext db,
        IReadOnlyList<ulong> demoIds,
        IReadOnlyDictionary<ulong, HashSet<int>> demoUserIds,
        IReadOnlyDictionary<ulong, DemoMeta> demoMetaById,
        IReadOnlyDictionary<ulong, double> demoDateById,
        IQueryable<TargetUserEntry> yearUsersQuery,
        CancellationToken cancellationToken)
    {
        var spawns = await db.StvSpawns
            .AsNoTracking()
            .Join(yearUsersQuery,
                spawn => new { spawn.DemoId, UserId = spawn.UserId },
                user => new { user.DemoId, UserId = user.UserId },
                (spawn, _) => new SpawnEvent(spawn.DemoId, spawn.UserId, spawn.Tick, spawn.Class, spawn.Team))
            .ToListAsync(cancellationToken);

        var deaths = await db.StvDeaths
            .AsNoTracking()
            .Join(yearUsersQuery,
                death => new { death.DemoId, UserId = death.VictimUserId },
                user => new { user.DemoId, UserId = user.UserId },
                (death, _) => new DeathEvent(death.DemoId, death.VictimUserId, death.Tick))
            .ToListAsync(cancellationToken);

        var teamChanges = await db.StvTeamChanges
            .AsNoTracking()
            .Join(yearUsersQuery,
                change => new { change.DemoId, UserId = change.UserId },
                user => new { user.DemoId, UserId = user.UserId },
                (change, _) => new TeamChangeEvent(change.DemoId, change.UserId, change.Tick, change.Team,
                    change.Disconnect))
            .ToListAsync(cancellationToken);

        var spawnsByDemo = spawns
            .GroupBy(spawn => spawn.DemoId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<SpawnEvent>)group.ToList());
        var deathsByDemo = deaths
            .GroupBy(death => death.DemoId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<DeathEvent>)group.ToList());
        var teamsByDemo = teamChanges
            .GroupBy(change => change.DemoId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<TeamChangeEvent>)group.ToList());

        var result = new PlaytimeResult();

        foreach (var demoId in demoIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!demoMetaById.TryGetValue(demoId, out var meta))
            {
                continue;
            }

            if (!demoUserIds.TryGetValue(demoId, out var userIds))
            {
                continue;
            }

            IReadOnlyList<SpawnEvent> demoSpawns = spawnsByDemo.TryGetValue(demoId, out var spawnList)
                ? spawnList
                : Array.Empty<SpawnEvent>();
            IReadOnlyList<DeathEvent> demoDeaths = deathsByDemo.TryGetValue(demoId, out var deathList)
                ? deathList
                : Array.Empty<DeathEvent>();
            IReadOnlyList<TeamChangeEvent> demoTeams = teamsByDemo.TryGetValue(demoId, out var teamList)
                ? teamList
                : Array.Empty<TeamChangeEvent>();

            if (!TryComputeDemoPlaytime(meta, userIds, demoSpawns, demoDeaths, demoTeams, out var totals))
            {
                continue;
            }

            result.DemosWithTime += 1;
            result.TotalSeconds += totals.TotalSeconds;
            result.SoldierSeconds += totals.SoldierSeconds;
            result.DemoSeconds += totals.DemoSeconds;
            result.ByDemoSeconds[demoId] = totals.TotalSeconds;

            AddSeconds(result.ByMapSeconds, meta.Map, totals.TotalSeconds);
            AddSeconds(result.ByServerSeconds, meta.Server, totals.TotalSeconds);

            if (demoDateById.TryGetValue(demoId, out var ts))
            {
                var dtUtc = GetUtcDateFromTimestamp(ts);
                AddSeconds(result.ByDowSeconds, dtUtc.ToString("ddd", CultureInfo.InvariantCulture), totals.TotalSeconds);
                AddSeconds(result.ByMonthSeconds, dtUtc.ToString("yyyy-MM", CultureInfo.InvariantCulture), totals.TotalSeconds);
                AddSeconds(result.ByDaySeconds, dtUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), totals.TotalSeconds);
                AddSeconds(result.ByHourSeconds, dtUtc.ToString("HH", CultureInfo.InvariantCulture), totals.TotalSeconds);
            }
        }

        return result;
    }

    private static void AddSeconds(Dictionary<string, double> dict, string key, double seconds)
    {
        if (string.IsNullOrWhiteSpace(key) || seconds <= 0)
        {
            return;
        }

        dict.TryGetValue(key, out var current);
        dict[key] = current + seconds;
    }

    private static bool TryComputeDemoPlaytime(DemoMeta meta, HashSet<int> userIds,
        IReadOnlyList<SpawnEvent> spawns,
        IReadOnlyList<DeathEvent> deaths,
        IReadOnlyList<TeamChangeEvent> teamChanges,
        out DemoPlaytimeTotals totals)
    {
        totals = new DemoPlaytimeTotals();

        if (!meta.IntervalPerTick.HasValue || meta.IntervalPerTick.Value <= 0)
        {
            return false;
        }

        var demoEndTick = meta.HeaderTicks ?? 0;
        if (demoEndTick <= 0)
        {
            demoEndTick = Math.Max(GetMaxTick(spawns), Math.Max(GetMaxTick(deaths), GetMaxTick(teamChanges)));
        }

        if (demoEndTick <= 0)
        {
            return false;
        }

        var hadTime = false;
        foreach (var userId in userIds)
        {
            var events = BuildPlayerEvents(userId, spawns, deaths, teamChanges);
            if (events.Count == 0)
            {
                continue;
            }

            hadTime |= AccumulateEvents(events, demoEndTick, meta.IntervalPerTick.Value, totals);
        }

        return hadTime;
    }

    private static List<PlayerEvent> BuildPlayerEvents(int userId, IReadOnlyList<SpawnEvent> spawns,
        IReadOnlyList<DeathEvent> deaths, IReadOnlyList<TeamChangeEvent> teamChanges)
    {
        var events = new List<PlayerEvent>();
        foreach (var spawn in spawns)
        {
            if (spawn.UserId == userId)
            {
                events.Add(new PlayerEvent(spawn.Tick, EventKind.Spawn, spawn.Class));
            }
        }

        foreach (var death in deaths)
        {
            if (death.UserId == userId)
            {
                events.Add(new PlayerEvent(death.Tick, EventKind.Death, null));
            }
        }

        foreach (var change in teamChanges)
        {
            if (change.UserId == userId && IsSpectatorEvent(change))
            {
                events.Add(new PlayerEvent(change.Tick, EventKind.Spectator, null));
            }
        }

        events.Sort((left, right) =>
        {
            var tickCompare = left.Tick.CompareTo(right.Tick);
            if (tickCompare != 0)
            {
                return tickCompare;
            }

            return left.Kind.CompareTo(right.Kind);
        });

        return events;
    }

    private static bool AccumulateEvents(IReadOnlyList<PlayerEvent> events, int demoEndTick, double intervalPerTick,
        DemoPlaytimeTotals totals)
    {
        var alive = false;
        var currentClass = string.Empty;
        var startTick = 0;
        var hasTime = false;

        foreach (var entry in events)
        {
            if (entry.Kind == EventKind.Spawn)
            {
                if (alive)
                {
                    hasTime |= AddInterval(totals, currentClass, startTick, entry.Tick, intervalPerTick);
                }

                var spawnClass = entry.Class ?? string.Empty;
                if (IsPlayableClass(spawnClass))
                {
                    alive = true;
                    currentClass = spawnClass;
                    startTick = entry.Tick;
                }
                else
                {
                    alive = false;
                }

                continue;
            }

            if (!alive)
            {
                continue;
            }

            hasTime |= AddInterval(totals, currentClass, startTick, entry.Tick, intervalPerTick);
            alive = false;
        }

        if (alive)
        {
            hasTime |= AddInterval(totals, currentClass, startTick, demoEndTick, intervalPerTick);
        }

        return hasTime;
    }

    private static bool AddInterval(DemoPlaytimeTotals totals, string className, int startTick, int endTick,
        double intervalPerTick)
    {
        var tickDelta = endTick - startTick;
        if (tickDelta <= 0)
        {
            return false;
        }

        var seconds = tickDelta * intervalPerTick;
        if (seconds <= 0)
        {
            return false;
        }

        totals.TotalSeconds += seconds;
        if (string.Equals(className, "soldier", StringComparison.OrdinalIgnoreCase))
        {
            totals.SoldierSeconds += seconds;
            return true;
        }

        if (string.Equals(className, "demoman", StringComparison.OrdinalIgnoreCase))
        {
            totals.DemoSeconds += seconds;
            return true;
        }

        return false;
    }

    private static bool IsPlayableClass(string className)
    {
        return !string.IsNullOrWhiteSpace(className) && PlayableClasses.Contains(className);
    }

    private static bool IsSpectatorEvent(TeamChangeEvent change)
    {
        if (change.Disconnect)
        {
            return true;
        }

        return string.Equals(change.Team, "spectator", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetMaxTick(IReadOnlyList<SpawnEvent> events)
    {
        return events.Count == 0 ? 0 : events.Max(entry => entry.Tick);
    }

    private static int GetMaxTick(IReadOnlyList<DeathEvent> events)
    {
        return events.Count == 0 ? 0 : events.Max(entry => entry.Tick);
    }

    private static int GetMaxTick(IReadOnlyList<TeamChangeEvent> events)
    {
        return events.Count == 0 ? 0 : events.Max(entry => entry.Tick);
    }

    private static IEnumerable<List<ulong>> ChunkIds(IReadOnlyList<ulong> demoIds, int size = 900)
    {
        for (var i = 0; i < demoIds.Count; i += size)
        {
            var chunk = demoIds.Skip(i).Take(size).ToList();
            if (chunk.Count == 0)
            {
                yield break;
            }

            yield return chunk;
        }
    }

    private static string FormatHours(double seconds)
    {
        return (seconds / 3600d).ToString("0.##", CultureInfo.InvariantCulture) + "h";
    }

    private static string BuildUserKey(long? steamId64, string steamId)
    {
        if (steamId64.HasValue)
        {
            return "64:" + steamId64.Value.ToString(CultureInfo.InvariantCulture);
        }

        return "id:" + (steamId ?? string.Empty).Trim().ToUpperInvariant();
    }

    private static async Task<List<FriendRow>> ComputeFriendsAsync(ArchiveDbContext db,
        IReadOnlyList<ulong> demoIds,
        IReadOnlyDictionary<ulong, HashSet<int>> targetDemoUserIds,
        IReadOnlyDictionary<ulong, double> playtimeByDemoSeconds,
        long? targetSteamId64,
        string targetSteamId,
        CancellationToken cancellationToken)
    {
        var targetKey = BuildUserKey(targetSteamId64, targetSteamId);

        var users = new List<DemoUserEntry>();
        foreach (var chunk in ChunkIds(demoIds))
        {
            var chunkUsers = await db.StvUsers
                .AsNoTracking()
                .Where(u => u.UserId != null)
                .Where(u => chunk.Contains(u.DemoId))
                .Select(u => new DemoUserEntry
                {
                    DemoId = u.DemoId,
                    UserId = u.UserId!.Value,
                    Name = u.Name,
                    SteamId64 = u.SteamId64,
                    SteamId = u.SteamIdClean ?? u.SteamId,
                    IsBot = u.IsBot
                })
                .ToListAsync(cancellationToken);
            users.AddRange(chunkUsers);
        }

        var totalsByKey = new Dictionary<string, FriendTotals>(StringComparer.Ordinal);

        // Build key lookup for chat-buddy analysis.
        var speakerKeyByDemoUserId = new Dictionary<ulong, Dictionary<int, string>>();
        foreach (var entry in users)
        {
            if (entry.IsBot == true)
            {
                continue;
            }

            var key = BuildUserKey(entry.SteamId64, entry.SteamId);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (!speakerKeyByDemoUserId.TryGetValue(entry.DemoId, out var byUserId))
            {
                byUserId = new Dictionary<int, string>();
                speakerKeyByDemoUserId[entry.DemoId] = byUserId;
            }

            byUserId[entry.UserId] = key;

            if (!totalsByKey.TryGetValue(key, out var totals))
            {
                totals = new FriendTotals(entry.SteamId64, entry.SteamId);
                totalsByKey[key] = totals;
            }

            totals.TrackName(entry.Name);
        }

        // Shared demos + shared playtime.
        foreach (var group in users.GroupBy(u => u.DemoId))
        {
            var demoId = group.Key;
            var demoSeconds = playtimeByDemoSeconds.TryGetValue(demoId, out var seconds) ? seconds : 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var user in group)
            {
                if (user.IsBot == true)
                {
                    continue;
                }

                var key = BuildUserKey(user.SteamId64, user.SteamId);
                if (string.IsNullOrWhiteSpace(key) || string.Equals(key, targetKey, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!seen.Add(key))
                {
                    continue;
                }

                if (!totalsByKey.TryGetValue(key, out var totals))
                {
                    totals = new FriendTotals(user.SteamId64, user.SteamId);
                    totalsByKey[key] = totals;
                }

                totals.SharedDemos += 1;
                totals.SharedPlaytimeSeconds += demoSeconds;
            }
        }

        // Chat messages per peer in shared demos.
        foreach (var chunk in ChunkIds(demoIds))
        {
            var chatCounts = await (
                from chat in db.StvChats.AsNoTracking()
                where chat.FromUserId != null && chunk.Contains(chat.DemoId)
                join user in db.StvUsers.AsNoTracking().Where(u => u.UserId != null && u.IsBot != true)
                    on new { chat.DemoId, UserId = chat.FromUserId!.Value }
                    equals new { user.DemoId, UserId = user.UserId!.Value }
                group chat by new { user.SteamId64, SteamId = user.SteamIdClean ?? user.SteamId }
                into g
                select new
                {
                    g.Key.SteamId64,
                    g.Key.SteamId,
                    Count = g.Count()
                }).ToListAsync(cancellationToken);

            foreach (var entry in chatCounts)
            {
                var key = BuildUserKey(entry.SteamId64, entry.SteamId);
                if (string.IsNullOrWhiteSpace(key) || string.Equals(key, targetKey, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!totalsByKey.TryGetValue(key, out var totals))
                {
                    totals = new FriendTotals(entry.SteamId64, entry.SteamId);
                    totalsByKey[key] = totals;
                }

                totals.ChatMessages += entry.Count;
            }
        }

        // Chat buddy counts (nearby messages within N lines).
        foreach (var chunk in ChunkIds(demoIds))
        {
            var lines = await db.StvChats
                .AsNoTracking()
                .Where(chat => chat.FromUserId != null)
                .Where(chat => chunk.Contains(chat.DemoId))
                .OrderBy(chat => chat.DemoId)
                .ThenBy(chat => chat.Index)
                .Select(chat => new ChatSpeakerLine
                {
                    DemoId = chat.DemoId,
                    UserId = chat.FromUserId!.Value
                })
                .ToListAsync(cancellationToken);

            foreach (var demoGroup in lines.GroupBy(line => line.DemoId))
            {
                if (!targetDemoUserIds.TryGetValue(demoGroup.Key, out var targetUserIds)
                    || targetUserIds.Count == 0)
                {
                    continue;
                }

                if (!speakerKeyByDemoUserId.TryGetValue(demoGroup.Key, out var speakerKeyByUserId))
                {
                    continue;
                }

                var ordered = demoGroup.ToList();
                for (var i = 0; i < ordered.Count; i++)
                {
                    var line = ordered[i];
                    if (!targetUserIds.Contains(line.UserId))
                    {
                        continue;
                    }

                    var forwardSeen = new HashSet<string>(StringComparer.Ordinal);
                    var backSeen = new HashSet<string>(StringComparer.Ordinal);

                    for (var j = i + 1; j < ordered.Count && j <= i + ChatBuddyWindow; j++)
                    {
                        var otherUserId = ordered[j].UserId;
                        if (targetUserIds.Contains(otherUserId))
                        {
                            continue;
                        }

                        if (!speakerKeyByUserId.TryGetValue(otherUserId, out var otherKey))
                        {
                            continue;
                        }

                        if (string.Equals(otherKey, targetKey, StringComparison.Ordinal) || !forwardSeen.Add(otherKey))
                        {
                            continue;
                        }

                        if (totalsByKey.TryGetValue(otherKey, out var totals))
                        {
                            totals.RepliesAfterYou += 1;
                        }
                    }

                    for (var j = Math.Max(0, i - ChatBuddyWindow); j < i; j++)
                    {
                        var otherUserId = ordered[j].UserId;
                        if (targetUserIds.Contains(otherUserId))
                        {
                            continue;
                        }

                        if (!speakerKeyByUserId.TryGetValue(otherUserId, out var otherKey))
                        {
                            continue;
                        }

                        if (string.Equals(otherKey, targetKey, StringComparison.Ordinal) || !backSeen.Add(otherKey))
                        {
                            continue;
                        }

                        if (totalsByKey.TryGetValue(otherKey, out var totals))
                        {
                            totals.RepliesBeforeYou += 1;
                        }
                    }
                }
            }
        }

        var rows = totalsByKey
            .Where(entry => entry.Value.SharedDemos > 0)
            .Select(entry => new FriendRow(
                entry.Value.DisplayName,
                entry.Value.SteamId64,
                entry.Value.SteamId,
                entry.Value.SharedDemos,
                entry.Value.SharedPlaytimeSeconds,
                entry.Value.ChatMessages,
                entry.Value.RepliesAfterYou,
                entry.Value.RepliesBeforeYou))
            .OrderByDescending(row => row.SharedPlaytimeSeconds)
            .ThenByDescending(row => row.SharedDemos)
            .ToList();

        return rows;
    }

    private static Dictionary<ulong, HashSet<string>> BuildNamesByDemo(IEnumerable<TargetUserEntry> targetUsers)
    {
        var result = new Dictionary<ulong, HashSet<string>>();
        foreach (var entry in targetUsers)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                continue;
            }

            if (!result.TryGetValue(entry.DemoId, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                result[entry.DemoId] = set;
            }

            set.Add(entry.Name.Trim());
        }

        return result;
    }

    private static bool IsMatchingName(string candidate, IReadOnlySet<string> names)
    {
        if (string.IsNullOrWhiteSpace(candidate) || names.Count == 0)
        {
            return false;
        }

        var normalized = candidate.Trim();
        if (names.Contains(normalized))
        {
            return true;
        }

        if (normalized.EndsWith("...", StringComparison.Ordinal) && normalized.Length > 3)
        {
            var prefix = normalized.Substring(0, normalized.Length - 3).Trim();
            if (prefix.Length == 0)
            {
                return false;
            }

            return names.Any(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    private static async Task<List<WrEvent>> ComputeWrEventsAsync(ArchiveDbContext db,
        IReadOnlyList<ulong> demoIds,
        IReadOnlyDictionary<ulong, DemoMeta> demoMetaById,
        IReadOnlyDictionary<ulong, double> demoDateById,
        IReadOnlyDictionary<ulong, HashSet<string>> targetNamesByDemoId,
        CancellationToken cancellationToken)
    {
        var events = new List<WrEvent>();

        foreach (var chunk in ChunkIds(demoIds))
        {
            var candidates = await db.StvChats
                .AsNoTracking()
                .Where(chat => chunk.Contains(chat.DemoId))
                .Where(chat =>
                    (EF.Functions.Like(chat.Text, "Tempus | (%")
                     && ((EF.Functions.Like(chat.Text, "%map run%") && EF.Functions.Like(chat.Text, "%WR%"))
                         || EF.Functions.Like(chat.Text, "%beat the map record%")
                         || EF.Functions.Like(chat.Text, "%set the first map record%")
                         || EF.Functions.Like(chat.Text, "%is ranked%with time%")
                         || EF.Functions.Like(chat.Text, "%set Bonus%")
                         || EF.Functions.Like(chat.Text, "%set Course%")
                         || EF.Functions.Like(chat.Text, "%set C%")
                         || EF.Functions.Like(chat.Text, "%broke%Bonus%")
                         || EF.Functions.Like(chat.Text, "%broke%Course%")
                         || EF.Functions.Like(chat.Text, "%broke C%")
                         || EF.Functions.Like(chat.Text, "% WR)%")))
                    || EF.Functions.Like(chat.Text, ":: (%"))
                .Select(chat => new WrChatCandidate
                {
                    DemoId = chat.DemoId,
                    Text = chat.Text
                })
                .ToListAsync(cancellationToken);

            foreach (var candidate in candidates)
            {
                if (!demoMetaById.TryGetValue(candidate.DemoId, out var meta))
                {
                    continue;
                }

                if (!targetNamesByDemoId.TryGetValue(candidate.DemoId, out var names) || names.Count == 0)
                {
                    continue;
                }

                if (!TryParseWrEvent(candidate.Text, meta.Map, out var parsed) || parsed is null)
                {
                    continue;
                }

                if (!IsMatchingName(parsed.Player, names))
                {
                    continue;
                }

                if (!demoDateById.TryGetValue(candidate.DemoId, out var ts))
                {
                    continue;
                }

                var dtUtc = GetUtcDateFromTimestamp(ts);
                events.Add(new WrEvent(dtUtc, candidate.DemoId, parsed.Map ?? meta.Map, parsed.Class, parsed.Source,
                    parsed.RecordTime, parsed.RunTime, parsed.Split, parsed.Improvement, parsed.Inferred, candidate.Text));
            }
        }

        events.Sort((left, right) =>
        {
            var dateCompare = left.TimestampUtc.CompareTo(right.TimestampUtc);
            if (dateCompare != 0)
            {
                return dateCompare;
            }

            var demoCompare = left.DemoId.CompareTo(right.DemoId);
            if (demoCompare != 0)
            {
                return demoCompare;
            }

            return string.Compare(left.RawText, right.RawText, StringComparison.Ordinal);
        });

        return events;
    }

    private static string GetWrBucketKey(WrEvent wr)
    {
        var source = NormalizeWrSource(wr.Source);
        if (string.Equals(source, "MapRecord", StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, "FirstRecord", StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, "MapRun", StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, "Ranked", StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, "IRC", StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, "Compact", StringComparison.OrdinalIgnoreCase))
        {
            source = "Map";
        }

        return wr.Map + "|" + wr.Class + "|" + source;
    }

    private static string NormalizeWrSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return string.Empty;
        }

        var normalized = source.Trim();
        const string suffix = " First";
        if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Substring(0, normalized.Length - suffix.Length).Trim();
        }

        return normalized;
    }

    private static bool IsLookupWr(WrEvent wr)
    {
        return string.Equals(wr.Source, "Compact", StringComparison.OrdinalIgnoreCase);
    }

    private static List<WrEvent> DedupWrImprovements(IEnumerable<WrEvent> events)
    {
        var ordered = events
            .OrderBy(entry => entry.TimestampUtc)
            .ThenBy(entry => entry.DemoId)
            .ThenBy(entry => entry.Map, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.RawText, StringComparer.Ordinal)
            .ToList();

        var bestByBucket = new Dictionary<string, int>(StringComparer.Ordinal);
        var improvements = new List<WrEvent>();
        foreach (var wr in ordered)
        {
            if (!TryParseTimeCentiseconds(wr.RecordTime, out var centis))
            {
                continue;
            }

            var bucket = GetWrBucketKey(wr);
            if (!bestByBucket.TryGetValue(bucket, out var best))
            {
                bestByBucket[bucket] = centis;
                improvements.Add(wr);
                continue;
            }

            if (centis < best)
            {
                bestByBucket[bucket] = centis;
                improvements.Add(wr);
            }
        }

        return improvements;
    }

    private const string TimePattern = @"\d{1,2}:\d{2}(?::\d{2})?\.\d{2}";
    private const string SignedTimePattern = @"[+-]?" + TimePattern;

    private static readonly Regex MapRecordRegex = new(
        $@"^Tempus \| \((?<class>[^)]+)\) (?<player>.*?) beat the map record: (?<time>{TimePattern}) \((?:(?<label>WR|PR)\s*)?(?<split>{SignedTimePattern})\)(?: \| (?<improvement>{TimePattern}) improvement!?)?\!?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex MapRecordNoSplitRegex = new(
        $@"^Tempus \| \((?<class>[^)]+)\) (?<player>.*?) beat the map record: (?<time>{TimePattern})\!?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex FirstMapRecordRegex = new(
        $@"^Tempus \| \((?<class>[^)]+)\) (?<player>.*?) set the first map record: (?<time>{TimePattern})\!?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex BonusRecordRegex = new(
        $@"^Tempus \| \((?<class>[^)]+)\) (?<player>.*?) broke\s+(?:~[^~]+~\s*)*Bonus\s*#?(?<index>\d+) (?<time>{TimePattern}) \((?:(?<label>WR|PR)\s*)?(?<split>{SignedTimePattern})\)(?: \| (?<improvement>{TimePattern}) improvement!?)?\!?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex CourseRecordRegex = new(
        $@"^Tempus \| \((?<class>[^)]+)\) (?<player>.*?) broke\s+(?:~[^~]+~\s*)*Course\s*#?(?<index>\d+) (?<time>{TimePattern}) \((?:(?<label>WR|PR)\s*)?(?<split>{SignedTimePattern})\)(?: \| (?<improvement>{TimePattern}) improvement!?)?\!?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex CourseSegmentRecordRegex = new(
        $@"^Tempus \| \((?<class>[^)]+)\) (?<player>.*?) broke\s+(?:~[^~]+~\s*)*C(?<index>\d+)\s*-\s*(?<name>.+?) (?<time>{TimePattern}) \((?:(?<label>WR|PR)\s*)?(?<split>{SignedTimePattern})\)(?: \| (?<improvement>{TimePattern}) improvement!?)?\!?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex SetBonusRegex = new(
        $@"^Tempus \| \((?<class>[^)]+)\) (?<player>.*?) set Bonus\s*#?(?<index>\d+) (?<time>{TimePattern})\!?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex SetCourseRegex = new(
        $@"^Tempus \| \((?<class>[^)]+)\) (?<player>.*?) set Course\s*#?(?<index>\d+) (?<time>{TimePattern})\!?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex SetCourseSegmentRegex = new(
        $@"^Tempus \| \((?<class>[^)]+)\) (?<player>.*?) set\s+(?:~[^~]+~\s*)*C(?<index>\d+)\s*-\s*(?<name>.+?) (?<time>{TimePattern})\!?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex MapRunRegex = new(
        $@"^Tempus \| \((?<class>[^)]+)\) (?<player>.*?) map run (?<run>{TimePattern}) \((?:(?<label>WR|PR)\s*)?(?<split>{SignedTimePattern})\)(?: \| (?<improvement>{TimePattern}) improvement!?)?\!?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex RankedTimeRegex = new(
        $@"^Tempus \| \((?<class>[^)]+)\) (?<player>.*?) is ranked (?<rank>\d+)/(?<total>\d+) on (?<map>[^ ]+)(?:\s+(?<segment>Bonus|Course)\s+(?<index>\d+))? with time: (?<time>{TimePattern})\!?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex IrcWrRegex = new(
        $@"^:: \((?<class>[^)]+)\) (?<player>.+?) broke (?<map>[^ ]+) WR: (?<time>{TimePattern}) \((?:(?<label>WR|PR)\s*)?(?<split>{SignedTimePattern})\)\!?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex IrcSetRegex = new(
        $@"^:: \((?<class>[^)]+)\) (?<player>.+?) set (?<map>[^ ]+) WR: (?<time>{TimePattern})\!?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex CompactRecordRegex = new(
        $@"^Tempus \| \((?<class>[^)]+?) (?<label>WR|PR)\) (?<map>.+?) :: (?<time>{TimePattern}) :: (?<player>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static bool TryParseWrEvent(string text, string defaultMap, out ParsedWrEvent? parsed)
    {
        parsed = null;

        if (TryParseMapRecord(text, defaultMap, out parsed))
        {
            return true;
        }

        if (TryParseFirstRecord(text, defaultMap, out parsed))
        {
            return true;
        }

        if (TryParseBonus(text, defaultMap, out parsed))
        {
            return true;
        }

        if (TryParseSetBonus(text, defaultMap, out parsed))
        {
            return true;
        }

        if (TryParseCourse(text, defaultMap, out parsed))
        {
            return true;
        }

        if (TryParseSetCourse(text, defaultMap, out parsed))
        {
            return true;
        }

        if (TryParseCourseSegment(text, defaultMap, out parsed))
        {
            return true;
        }

        if (TryParseSetCourseSegment(text, defaultMap, out parsed))
        {
            return true;
        }

        if (TryParseMapRun(text, defaultMap, out parsed))
        {
            return true;
        }

        if (TryParseRanked(text, out parsed))
        {
            return true;
        }

        if (TryParseIrc(text, out parsed))
        {
            return true;
        }

        if (TryParseCompact(text, out parsed))
        {
            return true;
        }

        return false;
    }

    private static bool TryParseMapRecord(string text, string mapName, out ParsedWrEvent? parsed)
    {
        parsed = null;
        var match = MapRecordRegex.Match(text);
        if (!match.Success)
        {
            match = MapRecordNoSplitRegex.Match(text);
            if (!match.Success)
            {
                return false;
            }
        }

        var label = match.Groups["label"].Success ? match.Groups["label"].Value : "WR";
        if (!string.Equals(label, "WR", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var detectedClass = NormalizeClass(match.Groups["class"].Value);
        var player = match.Groups["player"].Value.Trim();
        var time = match.Groups["time"].Value;
        var split = match.Groups["split"].Success ? match.Groups["split"].Value : null;
        var improvement = match.Groups["improvement"].Success ? match.Groups["improvement"].Value : null;

        parsed = new ParsedWrEvent(player, detectedClass, mapName, "MapRecord", time, null, split, improvement, false);
        return true;
    }

    private static bool TryParseFirstRecord(string text, string mapName, out ParsedWrEvent? parsed)
    {
        parsed = null;
        var match = FirstMapRecordRegex.Match(text);
        if (!match.Success)
        {
            return false;
        }

        var detectedClass = NormalizeClass(match.Groups["class"].Value);
        var player = match.Groups["player"].Value.Trim();
        var time = match.Groups["time"].Value;
        parsed = new ParsedWrEvent(player, detectedClass, mapName, "FirstRecord", time, null, null, null, false);
        return true;
    }

    private static bool TryParseBonus(string text, string mapName, out ParsedWrEvent? parsed)
    {
        parsed = null;
        var match = BonusRecordRegex.Match(text);
        if (!match.Success)
        {
            return false;
        }

        var label = match.Groups["label"].Success ? match.Groups["label"].Value : "WR";
        if (!string.Equals(label, "WR", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var detectedClass = NormalizeClass(match.Groups["class"].Value);
        var player = match.Groups["player"].Value.Trim();
        var time = match.Groups["time"].Value;
        var index = match.Groups["index"].Value;
        var split = match.Groups["split"].Success ? match.Groups["split"].Value : null;
        var improvement = match.Groups["improvement"].Success ? match.Groups["improvement"].Value : null;
        parsed = new ParsedWrEvent(player, detectedClass, mapName, "Bonus " + index, time, null, split, improvement,
            false);
        return true;
    }

    private static bool TryParseSetBonus(string text, string mapName, out ParsedWrEvent? parsed)
    {
        parsed = null;
        var match = SetBonusRegex.Match(text);
        if (!match.Success)
        {
            return false;
        }

        var detectedClass = NormalizeClass(match.Groups["class"].Value);
        var player = match.Groups["player"].Value.Trim();
        var time = match.Groups["time"].Value;
        var index = match.Groups["index"].Value;
        parsed = new ParsedWrEvent(player, detectedClass, mapName, "Bonus " + index + " First", time, null, null, null,
            false);
        return true;
    }

    private static bool TryParseCourse(string text, string mapName, out ParsedWrEvent? parsed)
    {
        parsed = null;
        var match = CourseRecordRegex.Match(text);
        if (!match.Success)
        {
            return false;
        }

        var label = match.Groups["label"].Success ? match.Groups["label"].Value : "WR";
        if (!string.Equals(label, "WR", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var detectedClass = NormalizeClass(match.Groups["class"].Value);
        var player = match.Groups["player"].Value.Trim();
        var time = match.Groups["time"].Value;
        var index = match.Groups["index"].Value;
        var split = match.Groups["split"].Success ? match.Groups["split"].Value : null;
        var improvement = match.Groups["improvement"].Success ? match.Groups["improvement"].Value : null;
        parsed = new ParsedWrEvent(player, detectedClass, mapName, "Course " + index, time, null, split, improvement,
            false);
        return true;
    }

    private static bool TryParseSetCourse(string text, string mapName, out ParsedWrEvent? parsed)
    {
        parsed = null;
        var match = SetCourseRegex.Match(text);
        if (!match.Success)
        {
            return false;
        }

        var detectedClass = NormalizeClass(match.Groups["class"].Value);
        var player = match.Groups["player"].Value.Trim();
        var time = match.Groups["time"].Value;
        var index = match.Groups["index"].Value;
        parsed = new ParsedWrEvent(player, detectedClass, mapName, "Course " + index + " First", time, null, null, null,
            false);
        return true;
    }

    private static bool TryParseCourseSegment(string text, string mapName, out ParsedWrEvent? parsed)
    {
        parsed = null;
        var match = CourseSegmentRecordRegex.Match(text);
        if (!match.Success)
        {
            return false;
        }

        var label = match.Groups["label"].Success ? match.Groups["label"].Value : "WR";
        if (!string.Equals(label, "WR", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var detectedClass = NormalizeClass(match.Groups["class"].Value);
        var player = match.Groups["player"].Value.Trim();
        var time = match.Groups["time"].Value;
        var index = match.Groups["index"].Value;
        var name = match.Groups["name"].Value.Trim();
        var segment = "C" + index + " - " + name;
        var split = match.Groups["split"].Success ? match.Groups["split"].Value : null;
        var improvement = match.Groups["improvement"].Success ? match.Groups["improvement"].Value : null;
        parsed = new ParsedWrEvent(player, detectedClass, mapName, segment, time, null, split, improvement, false);
        return true;
    }

    private static bool TryParseSetCourseSegment(string text, string mapName, out ParsedWrEvent? parsed)
    {
        parsed = null;
        var match = SetCourseSegmentRegex.Match(text);
        if (!match.Success)
        {
            return false;
        }

        var detectedClass = NormalizeClass(match.Groups["class"].Value);
        var player = match.Groups["player"].Value.Trim();
        var time = match.Groups["time"].Value;
        var index = match.Groups["index"].Value;
        var name = match.Groups["name"].Value.Trim();
        var segment = "C" + index + " - " + name;
        parsed = new ParsedWrEvent(player, detectedClass, mapName, segment + " First", time, null, null, null, false);
        return true;
    }

    private static bool TryParseMapRun(string text, string mapName, out ParsedWrEvent? parsed)
    {
        parsed = null;
        var match = MapRunRegex.Match(text);
        if (!match.Success)
        {
            return false;
        }

        var label = match.Groups["label"].Value;
        if (!string.Equals(label, "WR", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var detectedClass = NormalizeClass(match.Groups["class"].Value);
        var player = match.Groups["player"].Value.Trim();
        var runTime = match.Groups["run"].Value;
        var splitRaw = match.Groups["split"].Value;
        var split = splitRaw;
        var improvement = match.Groups["improvement"].Success ? match.Groups["improvement"].Value : null;

        if (!TryComputeRecordTime(runTime, splitRaw, out var recordTime, out var inferred))
        {
            return false;
        }

        parsed = new ParsedWrEvent(player, detectedClass, mapName, "MapRun", recordTime, runTime, split, improvement,
            inferred);
        return true;
    }

    private static bool TryParseRanked(string text, out ParsedWrEvent? parsed)
    {
        parsed = null;
        var match = RankedTimeRegex.Match(text);
        if (!match.Success)
        {
            return false;
        }

        if (!int.TryParse(match.Groups["rank"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rank)
            || rank != 1)
        {
            return false;
        }

        var detectedClass = NormalizeClass(match.Groups["class"].Value);
        var player = match.Groups["player"].Value.Trim();
        var time = match.Groups["time"].Value;
        var mapName = match.Groups["map"].Value;
        var segment = match.Groups["segment"].Value;
        var index = match.Groups["index"].Value;
        var source = "Ranked";
        if (!string.IsNullOrWhiteSpace(segment))
        {
            source = string.IsNullOrWhiteSpace(index) ? segment : segment + " " + index;
        }

        parsed = new ParsedWrEvent(player, detectedClass, mapName, source, time, null, null, null, true);
        return true;
    }

    private static bool TryParseIrc(string text, out ParsedWrEvent? parsed)
    {
        parsed = null;
        var match = IrcWrRegex.Match(text);
        if (match.Success)
        {
            var label = match.Groups["label"].Success ? match.Groups["label"].Value : "WR";
            if (!string.Equals(label, "WR", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var detectedClass = NormalizeClass(match.Groups["class"].Value);
            var player = match.Groups["player"].Value.Trim();
            var map = match.Groups["map"].Value;
            var time = match.Groups["time"].Value;
            var split = match.Groups["split"].Success ? match.Groups["split"].Value : null;
            parsed = new ParsedWrEvent(player, detectedClass, map, "IRC", time, null, split, null, false);
            return true;
        }

        match = IrcSetRegex.Match(text);
        if (!match.Success)
        {
            return false;
        }

        var detectedClass2 = NormalizeClass(match.Groups["class"].Value);
        var player2 = match.Groups["player"].Value.Trim();
        var map2 = match.Groups["map"].Value;
        var time2 = match.Groups["time"].Value;
        parsed = new ParsedWrEvent(player2, detectedClass2, map2, "IRC", time2, null, null, null, false);
        return true;
    }

    private static bool TryParseCompact(string text, out ParsedWrEvent? parsed)
    {
        parsed = null;
        var match = CompactRecordRegex.Match(text);
        if (!match.Success)
        {
            return false;
        }

        var label = match.Groups["label"].Value;
        if (!string.Equals(label, "WR", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var detectedClass = NormalizeClass(match.Groups["class"].Value);
        var player = match.Groups["player"].Value.Trim();
        var map = match.Groups["map"].Value;
        var time = match.Groups["time"].Value;
        parsed = new ParsedWrEvent(player, detectedClass, map, "Compact", time, null, null, null, false);
        return true;
    }

    private static string NormalizeClass(string value)
    {
        if (string.Equals(value, "Soldier", StringComparison.OrdinalIgnoreCase))
        {
            return "Solly";
        }

        if (string.Equals(value, "Demoman", StringComparison.OrdinalIgnoreCase))
        {
            return "Demo";
        }

        return value.Trim();
    }

    private static bool TryComputeRecordTime(string runTime, string splitRaw, out string recordTime, out bool inferred)
    {
        recordTime = string.Empty;
        inferred = false;

        if (!TryParseTimeCentiseconds(runTime, out var runCentis))
        {
            return false;
        }

        if (!TryParseSignedTimeCentiseconds(splitRaw, out var splitCentis, out var sign))
        {
            return false;
        }

        if (sign == 0)
        {
            return false;
        }

        if (sign < 0)
        {
            recordTime = runTime;
            inferred = false;
            return true;
        }

        var inferredCentis = runCentis - splitCentis;
        if (inferredCentis <= 0)
        {
            return false;
        }

        recordTime = FormatTimeFromCentiseconds(inferredCentis);
        inferred = true;
        return true;
    }

    private static bool TryParseTimeCentiseconds(string value, out int centiseconds)
    {
        centiseconds = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Trim().Split(':');
        if (parts.Length is < 2 or > 3)
        {
            return false;
        }

        var hours = 0;
        var minutesPartIndex = 0;
        if (parts.Length == 3)
        {
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out hours) || hours < 0)
            {
                return false;
            }

            minutesPartIndex = 1;
        }

        if (!int.TryParse(parts[minutesPartIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
            || minutes < 0)
        {
            return false;
        }

        var secondsParts = parts[minutesPartIndex + 1].Split('.');
        if (secondsParts.Length != 2)
        {
            return false;
        }

        if (!int.TryParse(secondsParts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            || seconds < 0)
        {
            return false;
        }

        if (!int.TryParse(secondsParts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var centis)
            || centis < 0 || centis > 99)
        {
            return false;
        }

        var totalSeconds = hours * 3600 + minutes * 60 + seconds;
        centiseconds = totalSeconds * 100 + centis;
        return true;
    }

    private static bool TryParseSignedTimeCentiseconds(string value, out int centiseconds, out int sign)
    {
        centiseconds = 0;
        sign = 0;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var raw = value.Trim();
        if (raw.StartsWith('+'))
        {
            sign = 1;
            raw = raw.Substring(1);
        }
        else if (raw.StartsWith('-'))
        {
            sign = -1;
            raw = raw.Substring(1);
        }
        else
        {
            return false;
        }

        return TryParseTimeCentiseconds(raw, out centiseconds);
    }

    private static string FormatTimeFromCentiseconds(int centiseconds)
    {
        if (centiseconds < 0)
        {
            centiseconds = -centiseconds;
        }

        var totalSeconds = centiseconds / 100;
        var cs = centiseconds % 100;
        var seconds = totalSeconds % 60;
        var totalMinutes = totalSeconds / 60;
        var minutes = totalMinutes % 60;
        var hours = totalMinutes / 60;

        if (hours > 0)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}.{3:00}", hours, minutes, seconds,
                cs);
        }

        return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}.{2:00}", minutes, seconds, cs);
    }

    private sealed record ParsedWrEvent(string Player, string Class, string? Map, string Source, string RecordTime,
        string? RunTime, string? Split, string? Improvement, bool Inferred);

    private sealed record WrEvent(DateTime TimestampUtc, ulong DemoId, string Map, string Class, string Source,
        string RecordTime, string? RunTime, string? Split, string? Improvement, bool Inferred, string RawText);

    private sealed class WrChatCandidate
    {
        public ulong DemoId { get; init; }
        public string Text { get; init; } = string.Empty;
    }

    private sealed class DemoUserEntry
    {
        public ulong DemoId { get; init; }
        public int UserId { get; init; }
        public string Name { get; init; } = string.Empty;
        public long? SteamId64 { get; init; }
        public string SteamId { get; init; } = string.Empty;
        public bool? IsBot { get; init; }
    }

    private sealed class FriendTotals
    {
        public FriendTotals(long? steamId64, string steamId)
        {
            SteamId64 = steamId64;
            SteamId = steamId;
        }

        public long? SteamId64 { get; }
        public string SteamId { get; }
        public int SharedDemos { get; set; }
        public double SharedPlaytimeSeconds { get; set; }
        public int ChatMessages { get; set; }
        public int RepliesAfterYou { get; set; }
        public int RepliesBeforeYou { get; set; }

        private readonly Dictionary<string, int> _nameCounts = new(StringComparer.OrdinalIgnoreCase);

        public void TrackName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            _nameCounts.TryGetValue(name, out var current);
            _nameCounts[name] = current + 1;
        }

        public string DisplayName => _nameCounts
            .OrderByDescending(entry => entry.Value)
            .Select(entry => entry.Key)
            .FirstOrDefault() ?? (SteamId64?.ToString(CultureInfo.InvariantCulture) ?? SteamId);
    }

    private sealed record FriendRow(
        string Name,
        long? SteamId64,
        string SteamId,
        int SharedDemos,
        double SharedPlaytimeSeconds,
        int ChatMessages,
        int RepliesAfterYou,
        int RepliesBeforeYou);

    private sealed class ChatSpeakerLine
    {
        public ulong DemoId { get; init; }
        public int UserId { get; init; }
    }

    private sealed class SentimentAggregate
    {
        public double Sum { get; set; }
        public int Count { get; set; }
    }

    private sealed record DemoMeta(ulong DemoId, string Map, string Server, double? IntervalPerTick, int? HeaderTicks);

    private sealed class PlaytimeResult
    {
        public int DemosWithTime { get; set; }
        public double TotalSeconds { get; set; }
        public double SoldierSeconds { get; set; }
        public double DemoSeconds { get; set; }

        public Dictionary<ulong, double> ByDemoSeconds { get; } = new();
        public Dictionary<string, double> ByMapSeconds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> ByServerSeconds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> ByDowSeconds { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, double> ByMonthSeconds { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, double> ByDaySeconds { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, double> ByHourSeconds { get; } = new(StringComparer.Ordinal);
    }

    private sealed class TargetUserEntry
    {
        public ulong DemoId { get; init; }
        public int UserId { get; init; }
        public string Name { get; init; } = string.Empty;
        public long? SteamId64 { get; init; }
        public string SteamId { get; init; } = string.Empty;
        public double DemoDate { get; init; }
    }

    private sealed class ChatEventRow
    {
        public double Timestamp { get; init; }
        public ulong DemoId { get; init; }
        public int ChatIndex { get; init; }
        public int? Tick { get; init; }
        public string Text { get; init; } = string.Empty;
    }

    private sealed record ScoredChat(DateTime TimestampUtc, ulong DemoId, string Map, string Server, string FullText,
        string Body, double Sentiment);

    private sealed record DemoPlaytimeTotals
    {
        public double SoldierSeconds { get; set; }
        public double DemoSeconds { get; set; }
        public double TotalSeconds { get; set; }
    }

    private sealed record SpawnEvent(ulong DemoId, int UserId, int Tick, string Class, string Team);

    private sealed record DeathEvent(ulong DemoId, int UserId, int Tick);

    private sealed record TeamChangeEvent(ulong DemoId, int UserId, int Tick, string Team, bool Disconnect);

    private sealed record PlayerEvent(int Tick, EventKind Kind, string? Class);

    private enum EventKind
    {
        Spectator = 0,
        Death = 1,
        Spawn = 2
    }

    private sealed class WrappedChatRow
    {
        public double Timestamp { get; init; }
        public ulong DemoId { get; init; }
        public int ChatIndex { get; init; }
        public int? Tick { get; init; }
        public required string Map { get; init; }
        public required string Server { get; init; }
        public required string Text { get; init; }
        public required string UserName { get; init; }
        public string? SteamId { get; init; }
        public long? SteamId64 { get; init; }
    }
}
