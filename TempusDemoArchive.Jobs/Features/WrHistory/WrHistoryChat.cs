using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TempusDemoArchive.Persistence.Models.STVs;

namespace TempusDemoArchive.Jobs;

internal static class WrHistoryChat
{
    internal readonly record struct ChatCandidate(ulong DemoId, string? Map, string Text, int ChatIndex, int? Tick,
        int? FromUserId);

    internal sealed record DemoUsers(Dictionary<int, UserIdentity> ByUserId,
        Dictionary<string, List<UserIdentity>> ByName);

    public static WrHistoryEntry? TryParseTempusRecord(ChatCandidate candidate, string mapInput,
        IReadOnlyDictionary<ulong, DateTime?> demoDates, IReadOnlyDictionary<ulong, DemoUsers> demoUsers)
    {
        if (TryParseCompactRecord(candidate, mapInput, demoDates, demoUsers, out var compactEntry))
        {
            return compactEntry;
        }

        if (TryParseFirstRecord(candidate, mapInput, demoDates, demoUsers, out var firstEntry))
        {
            return firstEntry;
        }

        if (TryParseMapRecord(candidate, mapInput, demoDates, demoUsers, out var mapEntry))
        {
            return mapEntry;
        }

        if (TryParseSetBonusRecord(candidate, mapInput, demoDates, demoUsers, out var setBonusEntry))
        {
            return setBonusEntry;
        }

        if (TryParseSetCourseRecord(candidate, mapInput, demoDates, demoUsers, out var setCourseEntry))
        {
            return setCourseEntry;
        }

        if (TryParseSetCourseSegmentRecord(candidate, mapInput, demoDates, demoUsers, out var setCourseSegmentEntry))
        {
            return setCourseSegmentEntry;
        }

        if (TryParseBonusRecord(candidate, mapInput, demoDates, demoUsers, out var bonusEntry))
        {
            return bonusEntry;
        }

        if (TryParseCourseRecord(candidate, mapInput, demoDates, demoUsers, out var courseEntry))
        {
            return courseEntry;
        }

        if (TryParseCourseSegmentRecord(candidate, mapInput, demoDates, demoUsers, out var courseSegmentEntry))
        {
            return courseSegmentEntry;
        }

        if (TryParseMapRun(candidate, mapInput, demoDates, demoUsers, out var runEntry))
        {
            return runEntry;
        }

        if (TryParseRankedRecord(candidate, mapInput, demoDates, demoUsers, out var rankedEntry))
        {
            return rankedEntry;
        }

        return null;
    }

    public static WrHistoryEntry? TryParseIrcRecord(ChatCandidate candidate, string? map,
        IReadOnlyDictionary<ulong, DateTime?> demoDates, IReadOnlyDictionary<ulong, DemoUsers> demoUsers)
    {
        var lastLine = candidate.Text.Split('\n').LastOrDefault() ?? string.Empty;
        var match = WrChatRegexes.IrcWr.Match(lastLine);
        var isSet = false;
        if (!match.Success)
        {
            match = WrChatRegexes.IrcSet.Match(lastLine);
            if (!match.Success)
            {
                return null;
            }

            isSet = true;
        }

        var mapName = match.Groups["map"].Value;
        if (!string.IsNullOrWhiteSpace(map) && !IsMapMatch(mapName, map))
        {
            return null;
        }

        var detectedClass = WrChatNormalizer.NormalizeClass(match.Groups["class"].Value);
        var player = match.Groups["player"].Value.Trim();
        var time = TempusTime.NormalizeTime(match.Groups["time"].Value);
        if (!IsValidRecordTime(time))
        {
            return null;
        }

        var split = match.Groups["split"].Success
            ? TempusTime.NormalizeSignedTime(match.Groups["split"].Value)
            : null;

        demoDates.TryGetValue(candidate.DemoId, out var date);
        var resolved = ResolveUserIdentity(candidate, player, demoUsers);
        var source = isSet ? WrHistoryConstants.Source.IrcSet : WrHistoryConstants.Source.Irc;
        return new WrHistoryEntry(player, detectedClass, mapName, WrHistoryConstants.RecordType.Wr, source, time, null,
            split,
            null, false, date, candidate.DemoId, resolved.Identity?.SteamId64, resolved.Identity?.SteamId,
            resolved.SteamCandidates, ChatIndex: candidate.ChatIndex, ChatTick: candidate.Tick);
    }

    public static WrHistoryEntry? TryParseCandidateAnyMap(ChatCandidate candidate,
        IReadOnlyDictionary<ulong, DateTime?> demoDates, IReadOnlyDictionary<ulong, DemoUsers> demoUsers)
    {
        if (!string.IsNullOrWhiteSpace(candidate.Map))
        {
            return TryParseTempusRecord(candidate, candidate.Map!, demoDates, demoUsers);
        }

        return TryParseIrcRecord(candidate, null, demoDates, demoUsers);
    }

    public static WrHistoryEntry? TryParseCandidateForMap(ChatCandidate candidate, string mapInput,
        IReadOnlyDictionary<ulong, DateTime?> demoDates, IReadOnlyDictionary<ulong, DemoUsers> demoUsers)
    {
        if (!string.IsNullOrWhiteSpace(candidate.Map))
        {
            return TryParseTempusRecord(candidate, mapInput, demoDates, demoUsers);
        }

        return TryParseIrcRecord(candidate, mapInput, demoDates, demoUsers);
    }

    public static bool IsSubRecordSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        return source.StartsWith(WrHistoryConstants.SegmentPrefix.Bonus, StringComparison.OrdinalIgnoreCase)
               || source.StartsWith(WrHistoryConstants.SegmentPrefix.Course, StringComparison.OrdinalIgnoreCase)
               || (source.StartsWith(WrHistoryConstants.SegmentPrefix.CourseSegment, StringComparison.OrdinalIgnoreCase)
                   && source.Length > 1
                   && char.IsDigit(source[1]))
               || source.StartsWith(WrHistoryConstants.Source.Ranked, StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<WrHistoryEntry> BuildWrHistory(IEnumerable<WrHistoryEntry> entries, bool includeAll)
    {
        // Build a stable, deterministic timeline:
        // - ordering: (date, demo_id, chat_index) to avoid reordering within a single day
        // - selection: keep only monotonic improvements per segment (Map / Bonus N / Course N / C#)
        // - evidence preference: when we have an in-demo record message for a time, suppress other
        //   evidence for that exact (segment,time) so the UI can link the real record-setting demo.

        var ordered = entries
            .Where(entry => entry.Date != null)
            .Select(entry =>
            {
                if (!TempusTime.TryParseTimeCentiseconds(entry.RecordTime, out var centiseconds))
                {
                    return (OrderedWrEntry?)null;
                }

                return new OrderedWrEntry(
                    Entry: entry,
                    Centiseconds: centiseconds,
                    Bucket: GetSegment(entry),
                    Priority: GetSourcePriority(entry));
            })
            .Where(entry => entry != null)
            .Select(entry => entry!.Value)
            .OrderBy(entry => entry.Entry.Date)
            .ThenBy(entry => entry.Entry.DemoId ?? ulong.MaxValue)
            .ThenBy(entry => entry.Entry.ChatIndex)
            .ThenBy(entry => entry.Priority)
            .ThenBy(entry => entry.Centiseconds)
            .ToList();

        // Record-setting messages are the only evidence that can be reliably tied to a record demo.
        // Other evidence (IRC, command output, inferred snapshots) can appear in arbitrary demos.
        var recordTimesByBucket = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in ordered)
        {
            if (!string.Equals(GetEvidenceKind(item.Entry), WrHistoryConstants.EvidenceKind.Record,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!recordTimesByBucket.TryGetValue(item.Bucket, out var recordTimes))
            {
                recordTimes = new HashSet<int>();
                recordTimesByBucket[item.Bucket] = recordTimes;
            }

            recordTimes.Add(item.Centiseconds);
        }

        var history = new List<WrHistoryEntry>();
        var bestByBucket = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in ordered)
        {
            var entry = item.Entry;
            var centiseconds = item.Centiseconds;
            var bucket = item.Bucket;

            var evidenceKind = GetEvidenceKind(entry);
            // If a record-setting demo exists for this exact time, avoid emitting duplicate evidence rows.
            if (!string.Equals(evidenceKind, WrHistoryConstants.EvidenceKind.Record, StringComparison.OrdinalIgnoreCase)
                && recordTimesByBucket.TryGetValue(bucket, out var recordTimes)
                && recordTimes.Contains(centiseconds))
            {
                continue;
            }

            var hasBest = bestByBucket.TryGetValue(bucket, out var best);
            // If a time is inferred/low-confidence, require it to beat the previous best by >= 1 centisecond.
            var epsilon = entry.Inferred ? 1 : 0;
            var improves = !hasBest || centiseconds < best - epsilon;

            if (includeAll)
            {
                if (!entry.Inferred)
                {
                    history.Add(entry);
                    if (improves)
                    {
                        bestByBucket[bucket] = centiseconds;
                    }

                    continue;
                }

                if (improves)
                {
                    history.Add(entry);
                    bestByBucket[bucket] = centiseconds;
                }

                continue;
            }

            if (improves)
            {
                history.Add(entry);
                bestByBucket[bucket] = centiseconds;
            }
        }

        return FixObservedWrAttribution(history, ordered);
    }

    private readonly record struct OrderedWrEntry(WrHistoryEntry Entry, int Centiseconds, string Bucket, int Priority);

    private readonly record struct WrHistoryKey(string Map, string Class, string Bucket, int Centiseconds);

    private static IEnumerable<WrHistoryEntry> FixObservedWrAttribution(
        IReadOnlyList<WrHistoryEntry> history,
        IReadOnlyList<OrderedWrEntry> ordered)
    {
        var holderByKey = new Dictionary<WrHistoryKey, WrHistoryEntry>();

        foreach (var item in ordered)
        {
            var entry = item.Entry;
            var centiseconds = item.Centiseconds;
            var bucket = item.Bucket;

            if (IsObservedWrSnapshot(entry))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.Player)
                || string.Equals(entry.Player, WrHistoryConstants.Unknown, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var key = new WrHistoryKey(entry.Map, entry.Class, bucket, centiseconds);
            if (!holderByKey.TryGetValue(key, out var existing))
            {
                holderByKey[key] = entry;
                continue;
            }

            if (IsBetterHolderCandidate(entry, existing))
            {
                holderByKey[key] = entry;
            }
        }

        foreach (var entry in history)
        {
            if (!TempusTime.TryParseTimeCentiseconds(entry.RecordTime, out var centiseconds))
            {
                yield return entry;
                continue;
            }

            var bucket = GetSegment(entry);
            var key = new WrHistoryKey(entry.Map, entry.Class, bucket, centiseconds);

            if (!IsObservedWrSnapshot(entry))
            {
                yield return entry;
                continue;
            }

            if (holderByKey.TryGetValue(key, out var holder))
            {
                yield return entry with
                {
                    Player = holder.Player,
                    Source = WrHistoryConstants.Source.ObservedWr,
                    RunTime = null,
                    Split = null,
                    Improvement = null,
                    DemoId = null,
                    SteamId64 = holder.SteamId64,
                    SteamId = holder.SteamId,
                    SteamCandidates = holder.SteamCandidates,
                    IsLookup = true
                };
                continue;
            }

            yield return entry with
            {
                Player = WrHistoryConstants.Unknown,
                Source = WrHistoryConstants.Source.ObservedWr,
                RunTime = null,
                Split = null,
                Improvement = null,
                DemoId = null,
                SteamId64 = null,
                SteamId = null,
                SteamCandidates = null,
                IsLookup = true
            };
        }
    }

    private static bool IsObservedWrSnapshot(WrHistoryEntry entry)
    {
        if (!entry.Inferred)
        {
            return false;
        }

        return string.Equals(entry.Source, WrHistoryConstants.Source.MapRun, StringComparison.OrdinalIgnoreCase)
               || string.Equals(entry.Source, WrHistoryConstants.Source.ObservedWr, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBetterHolderCandidate(WrHistoryEntry candidate, WrHistoryEntry existing)
    {
        var candidateRank = GetHolderCandidateRank(candidate);
        var existingRank = GetHolderCandidateRank(existing);
        if (candidateRank != existingRank)
        {
            return candidateRank < existingRank;
        }

        var candidateHasSteam = candidate.SteamId64.HasValue || !string.IsNullOrWhiteSpace(candidate.SteamId);
        var existingHasSteam = existing.SteamId64.HasValue || !string.IsNullOrWhiteSpace(existing.SteamId);
        if (candidateHasSteam != existingHasSteam)
        {
            return candidateHasSteam;
        }

        if (candidate.DemoId.HasValue != existing.DemoId.HasValue)
        {
            return candidate.DemoId.HasValue;
        }

        return (candidate.DemoId ?? ulong.MaxValue) < (existing.DemoId ?? ulong.MaxValue);
    }

    private static int GetHolderCandidateRank(WrHistoryEntry entry)
    {
        if (!entry.IsLookup && !entry.Inferred)
        {
            return 0;
        }

        if (!entry.IsLookup)
        {
            return 1;
        }

        if (!entry.Inferred)
        {
            return 2;
        }

        return 3;
    }

    public static async Task<Dictionary<ulong, DateTime?>> LoadDemoDatesAsync(ArchiveDbContext db,
        IReadOnlyList<ulong> demoIds, CancellationToken cancellationToken)
    {
        return await ArchiveQueries.LoadDemoDatesByIdAsync(db, demoIds, cancellationToken);
    }

    public static async Task<Dictionary<ulong, DemoUsers>> LoadDemoUsersAsync(ArchiveDbContext db,
        IReadOnlyList<ulong> demoIds, CancellationToken cancellationToken)
    {
        if (demoIds.Count == 0)
        {
            return new Dictionary<ulong, DemoUsers>();
        }

        var users = new List<StvUser>();
        foreach (var chunk in DbChunk.Chunk(demoIds))
        {
            var chunkUsers = await db.StvUsers
                .AsNoTracking()
                .Where(x => chunk.Contains(x.DemoId))
                .ToListAsync(cancellationToken);
            users.AddRange(chunkUsers);
        }

        return BuildDemoUsers(users);
    }

    public static Dictionary<ulong, DemoUsers> BuildDemoUsers(IEnumerable<StvUser> users)
    {
        var demoUsers = new Dictionary<ulong, DemoUsers>();
        foreach (var group in users.GroupBy(user => user.DemoId))
        {
            var byUserId = new Dictionary<int, UserIdentity>();
            var byName = new Dictionary<string, List<UserIdentity>>(StringComparer.OrdinalIgnoreCase);
            foreach (var user in group)
            {
                var identity = new UserIdentity(user.SteamId64, user.SteamIdClean ?? user.SteamId);
                if (user.UserId.HasValue && !byUserId.ContainsKey(user.UserId.Value))
                {
                    byUserId[user.UserId.Value] = identity;
                }

                var name = user.Name.Trim();
                if (name.Length == 0)
                {
                    continue;
                }

                if (!byName.TryGetValue(name, out var identities))
                {
                    identities = new List<UserIdentity>();
                    byName[name] = identities;
                }

                if (!identities.Any(existing => IsSameIdentity(existing, identity)))
                {
                    identities.Add(identity);
                }
            }

            demoUsers[group.Key] = new DemoUsers(byUserId, byName);
        }

        return demoUsers;
    }

    public static ResolvedIdentity ResolveUserIdentity(ChatCandidate candidate, string player,
        IReadOnlyDictionary<ulong, DemoUsers> demoUsers)
    {
        if (!demoUsers.TryGetValue(candidate.DemoId, out var users))
        {
            return new ResolvedIdentity(null, null);
        }

        if (candidate.FromUserId.HasValue
            && users.ByUserId.TryGetValue(candidate.FromUserId.Value, out var byUserId))
        {
            return new ResolvedIdentity(byUserId, null);
        }

        if (string.IsNullOrWhiteSpace(player))
        {
            return new ResolvedIdentity(null, null);
        }

        var normalized = player.Trim();
        if (users.ByName.TryGetValue(normalized, out var byName) && byName.Count > 0)
        {
            if (byName.Count == 1)
            {
                return new ResolvedIdentity(byName[0], null);
            }

            var candidates = BuildCandidates(normalized, byName)
                .Distinct(UserIdentityCandidateComparer.Instance)
                .ToList();
            return new ResolvedIdentity(null, SerializeSteamCandidates(candidates));
        }

        if (normalized.EndsWith("...", StringComparison.Ordinal) && normalized.Length > 3)
        {
            var prefix = normalized.Substring(0, normalized.Length - 3).Trim();
            if (prefix.Length > 0)
            {
                var matches = users.ByName
                    .Where(entry => entry.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(entry => BuildCandidates(entry.Key, entry.Value))
                    .Distinct(UserIdentityCandidateComparer.Instance)
                    .ToList();

                if (matches.Count == 1)
                {
                    var match = matches[0];
                    return new ResolvedIdentity(new UserIdentity(match.SteamId64, match.SteamId), null);
                }

                if (matches.Count > 1)
                {
                    return new ResolvedIdentity(null, SerializeSteamCandidates(matches));
                }
            }
        }

        return new ResolvedIdentity(null, null);
    }

    private static bool TryParseCompactRecord(ChatCandidate candidate, string mapInput,
        IReadOnlyDictionary<ulong, DateTime?> demoDates, IReadOnlyDictionary<ulong, DemoUsers> demoUsers,
        out WrHistoryEntry? entry)
    {
        entry = null;
        var match = WrChatRegexes.CompactRecord.Match(candidate.Text);
        if (!match.Success)
        {
            return false;
        }

        var detectedClass = WrChatNormalizer.NormalizeClass(match.Groups["class"].Value);
        var label = match.Groups["label"].Value;
        if (!IsWorldRecordLabel(label, allowEmpty: false))
        {
            return false;
        }

        label = label.ToUpperInvariant();
        var rawMap = match.Groups["map"].Value.Trim();
        var mapName = SplitMapSource(rawMap, out var source);
        if (!IsMapMatch(mapName, mapInput))
        {
            return false;
        }

        var time = TempusTime.NormalizeTime(match.Groups["time"].Value);
        if (!IsValidRecordTime(time))
        {
            return false;
        }

        var player = match.Groups["player"].Value.Trim();
        demoDates.TryGetValue(candidate.DemoId, out var date);
        var resolved = ResolveUserIdentity(candidate, player, demoUsers);
        entry = new WrHistoryEntry(player, detectedClass, mapName, label, source, time, null, null, null, false, date,
            candidate.DemoId, resolved.Identity?.SteamId64, resolved.Identity?.SteamId, resolved.SteamCandidates,
            IsLookup: true, ChatIndex: candidate.ChatIndex, ChatTick: candidate.Tick);
        return true;
    }

    private static bool TryParseFirstRecord(ChatCandidate candidate, string mapInput,
        IReadOnlyDictionary<ulong, DateTime?> demoDates, IReadOnlyDictionary<ulong, DemoUsers> demoUsers,
        out WrHistoryEntry? entry)
    {
        entry = null;
        var match = WrChatRegexes.FirstMapRecord.Match(candidate.Text);
        if (!match.Success)
        {
            return false;
        }

        var detectedClass = WrChatNormalizer.NormalizeClass(match.Groups["class"].Value);
        var player = match.Groups["player"].Value.Trim();
        var time = TempusTime.NormalizeTime(match.Groups["time"].Value);
        if (!IsValidRecordTime(time))
        {
            return false;
        }

        var mapName = candidate.Map ?? mapInput;
        demoDates.TryGetValue(candidate.DemoId, out var date);
        var resolved = ResolveUserIdentity(candidate, player, demoUsers);
        entry = new WrHistoryEntry(player, detectedClass, mapName, WrHistoryConstants.RecordType.Wr,
            WrHistoryConstants.Source.FirstRecord, time, null, null, null,
            false, date, candidate.DemoId, resolved.Identity?.SteamId64, resolved.Identity?.SteamId,
            resolved.SteamCandidates, ChatIndex: candidate.ChatIndex, ChatTick: candidate.Tick);
        return true;
    }

    private static bool TryParseMapRecord(ChatCandidate candidate, string mapInput,
        IReadOnlyDictionary<ulong, DateTime?> demoDates, IReadOnlyDictionary<ulong, DemoUsers> demoUsers,
        out WrHistoryEntry? entry)
    {
        entry = null;
        var match = WrChatRegexes.MapRecord.Match(candidate.Text);
        if (!match.Success)
        {
            match = WrChatRegexes.MapRecordNoSplit.Match(candidate.Text);
            if (!match.Success)
            {
                return false;
            }
        }

        var detectedClass = WrChatNormalizer.NormalizeClass(match.Groups["class"].Value);
        var player = match.Groups["player"].Value.Trim();
        var time = TempusTime.NormalizeTime(match.Groups["time"].Value);
        if (!IsValidRecordTime(time))
        {
            return false;
        }

        var split = match.Groups["split"].Success ? TempusTime.NormalizeSignedTime(match.Groups["split"].Value) : null;
        var improvement = match.Groups["improvement"].Success
            ? TempusTime.NormalizeTime(match.Groups["improvement"].Value)
            : null;

        var mapName = candidate.Map ?? mapInput;
        demoDates.TryGetValue(candidate.DemoId, out var date);
        var resolved = ResolveUserIdentity(candidate, player, demoUsers);
        entry = new WrHistoryEntry(player, detectedClass, mapName, WrHistoryConstants.RecordType.Wr,
            WrHistoryConstants.Source.MapRecord, time, null,
            split, improvement, false, date, candidate.DemoId, resolved.Identity?.SteamId64, resolved.Identity?.SteamId,
            resolved.SteamCandidates, ChatIndex: candidate.ChatIndex, ChatTick: candidate.Tick);
        return true;
    }

    private static bool TryParseBonusRecord(ChatCandidate candidate, string mapInput,
        IReadOnlyDictionary<ulong, DateTime?> demoDates, IReadOnlyDictionary<ulong, DemoUsers> demoUsers,
        out WrHistoryEntry? entry)
    {
        entry = null;
        var match = WrChatRegexes.BonusRecord.Match(candidate.Text);
        if (!match.Success)
        {
            return false;
        }

        var detectedClass = WrChatNormalizer.NormalizeClass(match.Groups["class"].Value);
        var player = match.Groups["player"].Value.Trim();
        var time = TempusTime.NormalizeTime(match.Groups["time"].Value);
        if (!IsValidRecordTime(time))
        {
            return false;
        }

        var split = TempusTime.NormalizeSignedTime(match.Groups["split"].Value);
        var improvement = match.Groups["improvement"].Success
            ? TempusTime.NormalizeTime(match.Groups["improvement"].Value)
            : null;

        var mapName = candidate.Map ?? mapInput;
        var source = WrHistoryConstants.SegmentPrefix.Bonus + " " + match.Groups["index"].Value;
        demoDates.TryGetValue(candidate.DemoId, out var date);
        var resolved = ResolveUserIdentity(candidate, player, demoUsers);
        entry = new WrHistoryEntry(player, detectedClass, mapName, WrHistoryConstants.RecordType.Wr, source, time, null,
            split,
            improvement, false, date, candidate.DemoId, resolved.Identity?.SteamId64, resolved.Identity?.SteamId,
            resolved.SteamCandidates, ChatIndex: candidate.ChatIndex, ChatTick: candidate.Tick);
        return true;
    }

    private static bool TryParseSetBonusRecord(ChatCandidate candidate, string mapInput,
        IReadOnlyDictionary<ulong, DateTime?> demoDates, IReadOnlyDictionary<ulong, DemoUsers> demoUsers,
        out WrHistoryEntry? entry)
    {
        entry = null;
        var match = WrChatRegexes.SetBonus.Match(candidate.Text);
        if (!match.Success)
        {
            return false;
        }

        var detectedClass = WrChatNormalizer.NormalizeClass(match.Groups["class"].Value);
        var player = match.Groups["player"].Value.Trim();
        var time = TempusTime.NormalizeTime(match.Groups["time"].Value);
        if (!IsValidRecordTime(time))
        {
            return false;
        }

        var mapName = candidate.Map ?? mapInput;
        var source = WrHistoryConstants.SegmentPrefix.Bonus + " " + match.Groups["index"].Value
                     + WrHistoryConstants.SegmentPrefix.FirstSuffix;

        demoDates.TryGetValue(candidate.DemoId, out var date);
        var resolved = ResolveUserIdentity(candidate, player, demoUsers);
        entry = new WrHistoryEntry(player, detectedClass, mapName, WrHistoryConstants.RecordType.Wr, source, time, null,
            null, null, false, date,
            candidate.DemoId, resolved.Identity?.SteamId64, resolved.Identity?.SteamId, resolved.SteamCandidates,
            ChatIndex: candidate.ChatIndex, ChatTick: candidate.Tick);
        return true;
    }

    private static bool TryParseCourseRecord(ChatCandidate candidate, string mapInput,
        IReadOnlyDictionary<ulong, DateTime?> demoDates, IReadOnlyDictionary<ulong, DemoUsers> demoUsers,
        out WrHistoryEntry? entry)
    {
        entry = null;
        var match = WrChatRegexes.CourseRecord.Match(candidate.Text);
        if (!match.Success)
        {
            return false;
        }

        var detectedClass = WrChatNormalizer.NormalizeClass(match.Groups["class"].Value);
        var player = match.Groups["player"].Value.Trim();
        var time = TempusTime.NormalizeTime(match.Groups["time"].Value);
        if (!IsValidRecordTime(time))
        {
            return false;
        }

        var split = TempusTime.NormalizeSignedTime(match.Groups["split"].Value);
        var improvement = match.Groups["improvement"].Success
            ? TempusTime.NormalizeTime(match.Groups["improvement"].Value)
            : null;

        var mapName = candidate.Map ?? mapInput;
        var source = WrHistoryConstants.SegmentPrefix.Course + " " + match.Groups["index"].Value;
        demoDates.TryGetValue(candidate.DemoId, out var date);
        var resolved = ResolveUserIdentity(candidate, player, demoUsers);
        entry = new WrHistoryEntry(player, detectedClass, mapName, WrHistoryConstants.RecordType.Wr, source, time, null,
            split,
            improvement, false, date, candidate.DemoId, resolved.Identity?.SteamId64, resolved.Identity?.SteamId,
            resolved.SteamCandidates, ChatIndex: candidate.ChatIndex, ChatTick: candidate.Tick);
        return true;
    }

    private static bool TryParseSetCourseRecord(ChatCandidate candidate, string mapInput,
        IReadOnlyDictionary<ulong, DateTime?> demoDates, IReadOnlyDictionary<ulong, DemoUsers> demoUsers,
        out WrHistoryEntry? entry)
    {
        entry = null;
        var match = WrChatRegexes.SetCourse.Match(candidate.Text);
        if (!match.Success)
        {
            return false;
        }

        var detectedClass = WrChatNormalizer.NormalizeClass(match.Groups["class"].Value);
        var player = match.Groups["player"].Value.Trim();
        var time = TempusTime.NormalizeTime(match.Groups["time"].Value);
        if (!IsValidRecordTime(time))
        {
            return false;
        }

        var mapName = candidate.Map ?? mapInput;
        var source = WrHistoryConstants.SegmentPrefix.Course + " " + match.Groups["index"].Value
                     + WrHistoryConstants.SegmentPrefix.FirstSuffix;

        demoDates.TryGetValue(candidate.DemoId, out var date);
        var resolved = ResolveUserIdentity(candidate, player, demoUsers);
        entry = new WrHistoryEntry(player, detectedClass, mapName, WrHistoryConstants.RecordType.Wr, source, time, null,
            null, null, false, date,
            candidate.DemoId, resolved.Identity?.SteamId64, resolved.Identity?.SteamId, resolved.SteamCandidates,
            ChatIndex: candidate.ChatIndex, ChatTick: candidate.Tick);
        return true;
    }

    private static bool TryParseSetCourseSegmentRecord(ChatCandidate candidate, string mapInput,
        IReadOnlyDictionary<ulong, DateTime?> demoDates, IReadOnlyDictionary<ulong, DemoUsers> demoUsers,
        out WrHistoryEntry? entry)
    {
        entry = null;
        var match = WrChatRegexes.SetCourseSegment.Match(candidate.Text);
        if (!match.Success)
        {
            return false;
        }

        var detectedClass = WrChatNormalizer.NormalizeClass(match.Groups["class"].Value);
        var player = match.Groups["player"].Value.Trim();
        var time = TempusTime.NormalizeTime(match.Groups["time"].Value);
        if (!IsValidRecordTime(time))
        {
            return false;
        }

        var segment = WrHistoryConstants.SegmentPrefix.CourseSegment + match.Groups["index"].Value + " - "
                      + match.Groups["name"].Value.Trim();
        var mapName = candidate.Map ?? mapInput;

        demoDates.TryGetValue(candidate.DemoId, out var date);
        var resolved = ResolveUserIdentity(candidate, player, demoUsers);
        entry = new WrHistoryEntry(player, detectedClass, mapName, WrHistoryConstants.RecordType.Wr,
            segment + WrHistoryConstants.SegmentPrefix.FirstSuffix, time, null, null, null,
            false, date, candidate.DemoId, resolved.Identity?.SteamId64, resolved.Identity?.SteamId,
            resolved.SteamCandidates, ChatIndex: candidate.ChatIndex, ChatTick: candidate.Tick);
        return true;
    }

    private static bool TryParseCourseSegmentRecord(ChatCandidate candidate, string mapInput,
        IReadOnlyDictionary<ulong, DateTime?> demoDates, IReadOnlyDictionary<ulong, DemoUsers> demoUsers,
        out WrHistoryEntry? entry)
    {
        entry = null;
        var match = WrChatRegexes.CourseSegmentRecord.Match(candidate.Text);
        if (!match.Success)
        {
            return false;
        }

        var detectedClass = WrChatNormalizer.NormalizeClass(match.Groups["class"].Value);
        var player = match.Groups["player"].Value.Trim();
        var time = TempusTime.NormalizeTime(match.Groups["time"].Value);
        if (!IsValidRecordTime(time))
        {
            return false;
        }

        var split = TempusTime.NormalizeSignedTime(match.Groups["split"].Value);
        var improvement = match.Groups["improvement"].Success
            ? TempusTime.NormalizeTime(match.Groups["improvement"].Value)
            : null;

        var segment = WrHistoryConstants.SegmentPrefix.CourseSegment + match.Groups["index"].Value + " - "
                      + match.Groups["name"].Value.Trim();
        var mapName = candidate.Map ?? mapInput;
        demoDates.TryGetValue(candidate.DemoId, out var date);
        var resolved = ResolveUserIdentity(candidate, player, demoUsers);
        entry = new WrHistoryEntry(player, detectedClass, mapName, WrHistoryConstants.RecordType.Wr, segment, time, null,
            split,
            improvement, false, date, candidate.DemoId, resolved.Identity?.SteamId64, resolved.Identity?.SteamId,
            resolved.SteamCandidates, ChatIndex: candidate.ChatIndex, ChatTick: candidate.Tick);
        return true;
    }

    private static bool TryParseMapRun(ChatCandidate candidate, string mapInput,
        IReadOnlyDictionary<ulong, DateTime?> demoDates, IReadOnlyDictionary<ulong, DemoUsers> demoUsers,
        out WrHistoryEntry? entry)
    {
        entry = null;
        var match = WrChatRegexes.MapRun.Match(candidate.Text);
        if (!match.Success)
        {
            return false;
        }

        var detectedClass = WrChatNormalizer.NormalizeClass(match.Groups["class"].Value);
        var player = match.Groups["player"].Value.Trim();
        var runTime = TempusTime.NormalizeTime(match.Groups["run"].Value);
        var splitRaw = match.Groups["split"].Value;
        var split = TempusTime.NormalizeSignedTime(splitRaw);
        var improvement = match.Groups["improvement"].Success
            ? TempusTime.NormalizeTime(match.Groups["improvement"].Value)
            : null;
        var label = match.Groups["label"].Value;
        if (!IsWorldRecordLabel(label, allowEmpty: false))
        {
            return false;
        }

        if (!TryComputeRecordTime(runTime, splitRaw, out var recordTime, out var inferred))
        {
            return false;
        }

        if (inferred)
        {
            // "map run (WR +00:xx)" indicates the runner is behind the current WR.
            // It is not a record-set event and the WR demo is not the current demo.
            return false;
        }

        if (!IsValidRecordTime(recordTime))
        {
            return false;
        }

        var mapName = candidate.Map ?? mapInput;
        demoDates.TryGetValue(candidate.DemoId, out var date);
        var resolved = ResolveUserIdentity(candidate, player, demoUsers);
        entry = new WrHistoryEntry(player, detectedClass, mapName, WrHistoryConstants.RecordType.Wr,
            WrHistoryConstants.Source.MapRun, recordTime,
            runTime, split, improvement, inferred, date, candidate.DemoId, resolved.Identity?.SteamId64,
            resolved.Identity?.SteamId, resolved.SteamCandidates, ChatIndex: candidate.ChatIndex, ChatTick: candidate.Tick);
        return true;
    }

    private static bool TryParseRankedRecord(ChatCandidate candidate, string mapInput,
        IReadOnlyDictionary<ulong, DateTime?> demoDates, IReadOnlyDictionary<ulong, DemoUsers> demoUsers,
        out WrHistoryEntry? entry)
    {
        entry = null;
        var match = WrChatRegexes.RankedTime.Match(candidate.Text);
        if (!match.Success)
        {
            return false;
        }

        if (!int.TryParse(match.Groups["rank"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rank)
            || rank != 1)
        {
            return false;
        }

        var detectedClass = WrChatNormalizer.NormalizeClass(match.Groups["class"].Value);
        var player = match.Groups["player"].Value.Trim();
        var time = TempusTime.NormalizeTime(match.Groups["time"].Value);
        if (!IsValidRecordTime(time))
        {
            return false;
        }

        var mapName = match.Groups["map"].Value;
        var segment = match.Groups["segment"].Value;
        var index = match.Groups["index"].Value;
        var source = WrHistoryConstants.Source.Ranked;
        if (!string.IsNullOrWhiteSpace(segment))
        {
            source = segment + (string.IsNullOrWhiteSpace(index) ? string.Empty : " " + index);
        }

        if (!IsMapMatch(mapName, mapInput))
        {
            return false;
        }

        demoDates.TryGetValue(candidate.DemoId, out var date);
        var resolved = ResolveUserIdentity(candidate, player, demoUsers);
        entry = new WrHistoryEntry(player, detectedClass, mapName, WrHistoryConstants.RecordType.Wr, source, time, null,
            null, null, true, date,
            candidate.DemoId, resolved.Identity?.SteamId64, resolved.Identity?.SteamId, resolved.SteamCandidates,
            IsLookup: true, ChatIndex: candidate.ChatIndex, ChatTick: candidate.Tick);
        return true;
    }

    private static IEnumerable<UserIdentityCandidate> BuildCandidates(string name, IEnumerable<UserIdentity> identities)
    {
        foreach (var identity in identities)
        {
            yield return new UserIdentityCandidate(name, identity.SteamId64, identity.SteamId);
        }
    }

    private static string? SerializeSteamCandidates(IReadOnlyList<UserIdentityCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var serialized = new List<string>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var name = SanitizeCandidateToken(candidate.Name);
            var steamId64 = candidate.SteamId64?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            var steamId = SanitizeCandidateToken(candidate.SteamId ?? string.Empty);
            serialized.Add($"{name}|{steamId64}|{steamId}");
        }

        return string.Join(';', serialized);
    }

    private static string SanitizeCandidateToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Replace(',', ' ')
            .Replace('|', ' ')
            .Replace(';', ' ')
            .Trim();
    }

    private static bool IsSameIdentity(UserIdentity left, UserIdentity right)
    {
        if (left.SteamId64.HasValue && right.SteamId64.HasValue)
        {
            return left.SteamId64.Value == right.SteamId64.Value;
        }

        if (!string.IsNullOrWhiteSpace(left.SteamId) && !string.IsNullOrWhiteSpace(right.SteamId))
        {
            return string.Equals(left.SteamId, right.SteamId, StringComparison.OrdinalIgnoreCase);
        }

        return left.SteamId64 == right.SteamId64
               && string.Equals(left.SteamId, right.SteamId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMapMatch(string mapName, string mapInput)
    {
        return string.Equals(mapName, mapInput, StringComparison.OrdinalIgnoreCase)
               || mapName.StartsWith(mapInput + "_", StringComparison.OrdinalIgnoreCase);
    }

    internal static string GetSegment(WrHistoryEntry entry)
    {
        var source = entry.Source?.Trim();
        if (string.IsNullOrWhiteSpace(source))
        {
            return WrHistoryConstants.Segment.Map;
        }

        if (source.StartsWith(WrHistoryConstants.Source.Ranked, StringComparison.OrdinalIgnoreCase))
        {
            return WrHistoryConstants.Segment.Map;
        }

        if (!IsSubRecordSource(source))
        {
            return WrHistoryConstants.Segment.Map;
        }

        if (source.EndsWith(WrHistoryConstants.SegmentPrefix.FirstSuffix, StringComparison.OrdinalIgnoreCase))
        {
            source = source.Substring(0, source.Length - WrHistoryConstants.SegmentPrefix.FirstSuffix.Length)
                .TrimEnd();
        }

        return source;
    }

    internal static string GetEvidenceKind(WrHistoryEntry entry)
    {
        if (string.Equals(entry.Source, WrHistoryConstants.Source.ObservedWr, StringComparison.OrdinalIgnoreCase))
        {
            return WrHistoryConstants.EvidenceKind.Observed;
        }

        if (entry.IsLookup)
        {
            return WrHistoryConstants.EvidenceKind.Command;
        }

        if (!string.IsNullOrWhiteSpace(entry.Source)
            && entry.Source.StartsWith(WrHistoryConstants.Source.Irc, StringComparison.OrdinalIgnoreCase))
        {
            return WrHistoryConstants.EvidenceKind.Announcement;
        }

        return WrHistoryConstants.EvidenceKind.Record;
    }

    internal static string GetEvidenceSource(WrHistoryEntry entry)
    {
        if (string.Equals(entry.Source, WrHistoryConstants.Source.ObservedWr, StringComparison.OrdinalIgnoreCase))
        {
            return WrHistoryConstants.EvidenceSource.WrSplit;
        }

        if (entry.IsLookup)
        {
            return entry.Inferred ? WrHistoryConstants.EvidenceSource.RankCommand : WrHistoryConstants.EvidenceSource.WrCommand;
        }

        if (!string.IsNullOrWhiteSpace(entry.Source)
            && entry.Source.StartsWith(WrHistoryConstants.Source.Irc, StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(entry.Source, WrHistoryConstants.Source.IrcSet, StringComparison.OrdinalIgnoreCase)
                ? WrHistoryConstants.EvidenceSource.IrcSet
                : WrHistoryConstants.EvidenceSource.Irc;
        }

        if (string.Equals(entry.Source, WrHistoryConstants.Source.MapRecord, StringComparison.OrdinalIgnoreCase))
        {
            return WrHistoryConstants.EvidenceSource.MapRecord;
        }

        if (string.Equals(entry.Source, WrHistoryConstants.Source.FirstRecord, StringComparison.OrdinalIgnoreCase))
        {
            return WrHistoryConstants.EvidenceSource.FirstRecord;
        }

        if (string.Equals(entry.Source, WrHistoryConstants.Source.MapRun, StringComparison.OrdinalIgnoreCase))
        {
            return WrHistoryConstants.EvidenceSource.MapRun;
        }

        var segment = GetSegment(entry);
        if (!string.Equals(segment, WrHistoryConstants.Segment.Map, StringComparison.OrdinalIgnoreCase))
        {
            return entry.Source.EndsWith(WrHistoryConstants.SegmentPrefix.FirstSuffix, StringComparison.OrdinalIgnoreCase)
                ? WrHistoryConstants.EvidenceSource.ZoneFirst
                : WrHistoryConstants.EvidenceSource.ZoneRecord;
        }

        return WrHistoryConstants.EvidenceSource.Unknown;
    }

    internal static bool ShouldIncludeDemoLink(WrHistoryEntry entry)
    {
        if (!entry.DemoId.HasValue)
        {
            return false;
        }

        return string.Equals(GetEvidenceKind(entry), WrHistoryConstants.EvidenceKind.Record,
            StringComparison.OrdinalIgnoreCase);
    }

    private static int GetSourcePriority(WrHistoryEntry entry)
    {
        if (entry.Inferred)
        {
            return 100;
        }

        if (string.Equals(entry.Source, WrHistoryConstants.Source.MapRecord, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (string.Equals(entry.Source, WrHistoryConstants.Source.FirstRecord, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (string.Equals(entry.Source, WrHistoryConstants.Source.Compact, StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (entry.Source.StartsWith(WrHistoryConstants.Source.Irc, StringComparison.OrdinalIgnoreCase))
        {
            return 4;
        }

        if (string.Equals(entry.Source, WrHistoryConstants.Source.MapRun, StringComparison.OrdinalIgnoreCase))
        {
            return 5;
        }

        if (string.Equals(entry.Source, WrHistoryConstants.Source.Ranked, StringComparison.OrdinalIgnoreCase))
        {
            return 6;
        }

        return 50;
    }

    private static bool IsWorldRecordLabel(string label, bool allowEmpty)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return allowEmpty;
        }

        return string.Equals(label, WrHistoryConstants.Label.Wr, StringComparison.OrdinalIgnoreCase)
               || string.Equals(label, WrHistoryConstants.Label.Sr, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidRecordTime(string value)
    {
        return TempusTime.TryParseTimeCentiseconds(value, out var centiseconds) && centiseconds > 0;
    }

    private static string SplitMapSource(string rawMap, out string source)
    {
        source = WrHistoryConstants.Source.Compact;
        if (string.IsNullOrWhiteSpace(rawMap))
        {
            return rawMap;
        }

        var match = WrChatRegexes.CompactMapSource.Match(rawMap);
        if (!match.Success)
        {
            return rawMap;
        }

        var mapName = match.Groups["map"].Value;
        var type = match.Groups["type"].Value;
        var index = match.Groups["index"].Value;
        source = string.IsNullOrWhiteSpace(index) ? type : type + " " + index;
        return mapName;
    }

    private static bool TryComputeRecordTime(string runTime, string splitRaw,
        out string recordTime, out bool inferred)
    {
        recordTime = string.Empty;
        inferred = false;

        if (!TempusTime.TryParseTimeCentiseconds(runTime, out var runCentiseconds))
        {
            return false;
        }

        if (!TempusTime.TryParseSignedTimeCentiseconds(splitRaw, out var splitCentiseconds, out var sign))
        {
            return false;
        }

        if (sign == 0)
        {
            return false;
        }

        if (sign < 0)
        {
            recordTime = TempusTime.FormatTimeFromCentiseconds(runCentiseconds);
            inferred = false;
            return true;
        }

        var inferredCentiseconds = runCentiseconds - splitCentiseconds;
        if (inferredCentiseconds <= 0)
        {
            return false;
        }

        recordTime = TempusTime.FormatTimeFromCentiseconds(inferredCentiseconds);
        inferred = true;
        return true;
    }
}
