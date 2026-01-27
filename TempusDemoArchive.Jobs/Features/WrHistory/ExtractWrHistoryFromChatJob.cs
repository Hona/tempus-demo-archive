using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TempusDemoArchive.Persistence.Models.STVs;

namespace TempusDemoArchive.Jobs;

public class ExtractWrHistoryFromChatJob : IJob
{
    private const string TimePattern = TempusTime.TimePattern;
    private const string SignedTimePattern = TempusTime.SignedTimePattern;

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
    private static readonly Regex RankedTimeRegex = new(
        $@"^Tempus \| \((?<class>[^)]+)\) (?<player>.*?) is ranked (?<rank>\d+)/(?<total>\d+) on (?<map>[^ ]+)(?:\s+(?<segment>Bonus|Course)\s+(?<index>\d+))? with time: (?<time>{TimePattern})\!?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex MapRunRegex = new(
        $@"^Tempus \| \((?<class>[^)]+)\) (?<player>.*?) map run (?<run>{TimePattern}) \((?:(?<label>WR|PR)\s*)?(?<split>{SignedTimePattern})\)(?: \| (?<improvement>{TimePattern}) improvement!?)?\!?$",
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

    internal readonly record struct ChatCandidate(ulong DemoId, string? Map, string Text, int? FromUserId);
    internal sealed record DemoUsers(Dictionary<int, UserIdentity> ByUserId,
        Dictionary<string, List<UserIdentity>> ByName);

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine("Input map name: ");
        var map = Console.ReadLine()?.Trim();

        if (string.IsNullOrWhiteSpace(map))
        {
            Console.WriteLine("No map name provided.");
            return;
        }

        Console.WriteLine("Input class 'D' or 'S':");

        var @class = Console.ReadLine()!.ToUpper().Trim();

        if (@class != "D" && @class != "S")
        {
            throw new InvalidOperationException("Invalid class");
        }

        await using var db = new ArchiveDbContext();
        var mapPrefix = map + "_";

        var mapDemos = await db.Stvs
            .AsNoTracking()
            .Where(x => x.Header.Map == map || EF.Functions.Like(x.Header.Map, mapPrefix + "%"))
            .Select(x => new { x.DemoId, x.Header.Map })
            .ToListAsync(cancellationToken);
        var mapByDemoId = mapDemos.ToDictionary(x => x.DemoId, x => x.Map);
        var mapDemoIds = mapDemos.Select(x => x.DemoId).ToList();

        var mapsMatching = mapDemos
            .Select(x => x.Map)
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        foreach (var matchingMap in mapsMatching)
        {
            Console.WriteLine(matchingMap);
        }

        var suspectedWrMessages = new List<ChatCandidate>();
        foreach (var chunk in DbChunk.Chunk(mapDemoIds))
        {
            var chunkChats = await db.StvChats
                .AsNoTracking()
                .Where(chat => chunk.Contains(chat.DemoId))
                .Where(chat => EF.Functions.Like(chat.Text, "Tempus | (%"))
                .Where(chat => (EF.Functions.Like(chat.Text, "%map run%")
                                && EF.Functions.Like(chat.Text, "%WR%"))
                               || EF.Functions.Like(chat.Text, "%beat the map record%")
                               || EF.Functions.Like(chat.Text, "%set the first map record%")
                               || EF.Functions.Like(chat.Text, "%is ranked%with time%")
                               || EF.Functions.Like(chat.Text, "%set Bonus%")
                               || EF.Functions.Like(chat.Text, "%set Course%")
                               || EF.Functions.Like(chat.Text, "%set C%")
                               || EF.Functions.Like(chat.Text, "%broke%Bonus%")
                               || EF.Functions.Like(chat.Text, "%broke%Course%")
                               || EF.Functions.Like(chat.Text, "%broke C%")
                               || EF.Functions.Like(chat.Text, "% WR)%"))
                .Select(chat => new { chat.DemoId, chat.Text, chat.FromUserId })
                .ToListAsync(cancellationToken);

            foreach (var chat in chunkChats)
            {
                if (mapByDemoId.TryGetValue(chat.DemoId, out var mapName))
                {
                    suspectedWrMessages.Add(new ChatCandidate(chat.DemoId, mapName, chat.Text, chat.FromUserId));
                }
            }
        }

        var suspectedIrcWrMessages = await db.StvChats
            .AsNoTracking()
            .Where(chat => EF.Functions.Like(chat.Text, ":: Tempus -%")
                           || EF.Functions.Like(chat.Text, ":: (%"))
            .Where(chat => chat.Text.Contains(" WR: "))
            .Where(chat => chat.Text.Contains(" broke ") || chat.Text.Contains(" set "))
            .Where(chat => EF.Functions.Like(chat.Text, "% " + map + "%")
                           || EF.Functions.Like(chat.Text, "% " + mapPrefix + "%"))
            .Select(chat => new ChatCandidate(chat.DemoId, null, chat.Text, chat.FromUserId))
            .ToListAsync(cancellationToken);

        var demoIds = suspectedWrMessages.Select(x => x.DemoId)
            .Concat(suspectedIrcWrMessages.Select(x => x.DemoId))
            .Distinct()
            .ToList();

        var demoDates = await LoadDemoDatesAsync(db, demoIds, cancellationToken);
        var demoUsers = await LoadDemoUsersAsync(db, demoIds, cancellationToken);

        const string soldier = "Solly";
        const string demoman = "Demo";

        var output = new List<WrHistoryEntry>();
        foreach (var candidate in suspectedWrMessages)
        {
            var entry = TryParseTempusRecord(candidate, map, demoDates, demoUsers);
            if (entry != null)
            {
                output.Add(entry);
            }
        }

        foreach (var candidate in suspectedIrcWrMessages)
        {
            var entry = TryParseIrcRecord(candidate, map, demoDates, demoUsers);
            if (entry != null)
            {
                output.Add(entry);
            }
        }

        var classOutput = @class switch
        {
            "S" => output.Where(x => x.Class == soldier),
            "D" => output.Where(x => x.Class == demoman),
            _ => Enumerable.Empty<WrHistoryEntry>()
        };

        classOutput = classOutput.Where(x => string.Equals(x.RecordType, "WR", StringComparison.OrdinalIgnoreCase));

        var includeSubRecords = GetIncludeSubRecords();
        if (!includeSubRecords)
        {
            classOutput = classOutput.Where(entry => !IsSubRecordSource(entry.Source));
        }

        var includeInferred = GetIncludeInferred();
        if (!includeInferred)
        {
            classOutput = classOutput.Where(entry => !entry.Inferred);
        }

        var includeLookup = GetIncludeLookup();
        if (!includeLookup)
        {
            classOutput = classOutput.Where(entry => !entry.IsLookup);
        }

        var includeAll = GetIncludeAllEntries();
        var wrHistory = BuildWrHistory(classOutput, includeAll)
            .OrderBy(entry => entry.Date)
            .ThenBy(entry => entry.RecordTime)
            .ToList();

        var csvPath = WriteCsv(map, @class, wrHistory);

        foreach (var wrHistoryEntry in wrHistory)
        {
            var date = ArchiveUtils.FormatDate(wrHistoryEntry.Date);
            var steam = wrHistoryEntry.SteamId64?.ToString() ?? wrHistoryEntry.SteamId ?? "unknown";
            var inferred = wrHistoryEntry.Inferred ? " inferred" : string.Empty;
            var run = string.IsNullOrWhiteSpace(wrHistoryEntry.RunTime) ? string.Empty : $" run {wrHistoryEntry.RunTime}";
            var split = string.IsNullOrWhiteSpace(wrHistoryEntry.Split) ? string.Empty : $" split {wrHistoryEntry.Split}";
            var improvement = string.IsNullOrWhiteSpace(wrHistoryEntry.Improvement)
                ? string.Empty
                : $" imp {wrHistoryEntry.Improvement}";
            Console.WriteLine(
                $"{date} - {wrHistoryEntry.RecordType}/{wrHistoryEntry.Source}{inferred} - {wrHistoryEntry.RecordTime}{run}{split}{improvement} - {wrHistoryEntry.Player} ({wrHistoryEntry.Map}) ({wrHistoryEntry.DemoId}) [{steam}]");
        }

        Console.WriteLine($"CSV: {csvPath}");
    }

    internal static WrHistoryEntry? TryParseTempusRecord(ChatCandidate candidate, string mapInput,
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

    internal static WrHistoryEntry? TryParseIrcRecord(ChatCandidate candidate, string? map,
        IReadOnlyDictionary<ulong, DateTime?> demoDates, IReadOnlyDictionary<ulong, DemoUsers> demoUsers)
    {
        var lastLine = candidate.Text.Split('\n').LastOrDefault() ?? string.Empty;
        var match = IrcWrRegex.Match(lastLine);
        var isSet = false;
        if (!match.Success)
        {
            match = IrcSetRegex.Match(lastLine);
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

        var detectedClass = NormalizeClass(match.Groups["class"].Value);
        var player = match.Groups["player"].Value.Trim();
        var time = NormalizeTime(match.Groups["time"].Value);
        if (!IsValidRecordTime(time))
        {
            return null;
        }
        var split = match.Groups["split"].Success
            ? NormalizeSignedTime(match.Groups["split"].Value)
            : null;
        var label = match.Groups["label"].Value;
        if (!IsWorldRecordLabel(label, allowEmpty: true))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(label))
        {
            label = "WR";
        }

        demoDates.TryGetValue(candidate.DemoId, out var date);
        var resolved = ResolveUserIdentity(candidate, player, demoUsers);
        var source = isSet ? "IRCSet" : "IRC";
        return new WrHistoryEntry(player, detectedClass, mapName, label.ToUpperInvariant(), source, time, null, split,
            null, false, date, candidate.DemoId, resolved.Identity?.SteamId64, resolved.Identity?.SteamId,
            resolved.SteamCandidates);
    }

    private static bool TryParseCompactRecord(ChatCandidate candidate, string mapInput,
        IReadOnlyDictionary<ulong, DateTime?> demoDates, IReadOnlyDictionary<ulong, DemoUsers> demoUsers,
        out WrHistoryEntry? entry)
    {
        entry = null;
        var match = CompactRecordRegex.Match(candidate.Text);
        if (!match.Success)
        {
            return false;
        }

        if (match.Groups["trick"].Success && !string.IsNullOrWhiteSpace(match.Groups["trick"].Value))
        {
            return false;
        }

        var detectedClass = NormalizeClass(match.Groups["class"].Value);
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

        var time = NormalizeTime(match.Groups["time"].Value);
        if (!IsValidRecordTime(time))
        {
            return false;
        }
        var player = match.Groups["player"].Value.Trim();
        demoDates.TryGetValue(candidate.DemoId, out var date);
        var resolved = ResolveUserIdentity(candidate, player, demoUsers);
        entry = new WrHistoryEntry(player, detectedClass, mapName, label, source, time, null, null, null, false, date,
            candidate.DemoId, resolved.Identity?.SteamId64, resolved.Identity?.SteamId, resolved.SteamCandidates,
            IsLookup: true);
        return true;
    }

    private static bool TryParseFirstRecord(ChatCandidate candidate, string mapInput,
        IReadOnlyDictionary<ulong, DateTime?> demoDates, IReadOnlyDictionary<ulong, DemoUsers> demoUsers,
        out WrHistoryEntry? entry)
    {
        entry = null;
        var match = FirstMapRecordRegex.Match(candidate.Text);
        if (!match.Success)
        {
            return false;
        }

        var detectedClass = NormalizeClass(match.Groups["class"].Value);
        var player = match.Groups["player"].Value.Trim();
        var time = NormalizeTime(match.Groups["time"].Value);
        if (!IsValidRecordTime(time))
        {
            return false;
        }
        var mapName = candidate.Map ?? mapInput;

        demoDates.TryGetValue(candidate.DemoId, out var date);
        var resolved = ResolveUserIdentity(candidate, player, demoUsers);
        entry = new WrHistoryEntry(player, detectedClass, mapName, "WR", "FirstRecord", time, null, null, null,
            false, date, candidate.DemoId, resolved.Identity?.SteamId64, resolved.Identity?.SteamId,
            resolved.SteamCandidates);
        return true;
    }

    private static bool TryParseMapRecord(ChatCandidate candidate, string mapInput,
        IReadOnlyDictionary<ulong, DateTime?> demoDates, IReadOnlyDictionary<ulong, DemoUsers> demoUsers,
        out WrHistoryEntry? entry)
    {
        entry = null;
        var match = MapRecordRegex.Match(candidate.Text);
        if (!match.Success)
        {
            match = MapRecordNoSplitRegex.Match(candidate.Text);
            if (!match.Success)
            {
                return false;
            }
        }

        var detectedClass = NormalizeClass(match.Groups["class"].Value);
        var player = match.Groups["player"].Value.Trim();
        var time = NormalizeTime(match.Groups["time"].Value);
        if (!IsValidRecordTime(time))
        {
            return false;
        }
        var split = match.Groups["split"].Success ? NormalizeSignedTime(match.Groups["split"].Value) : null;
        var improvement = match.Groups["improvement"].Success
            ? NormalizeTime(match.Groups["improvement"].Value)
            : null;
        var label = match.Groups["label"].Value;
        if (!IsWorldRecordLabel(label, allowEmpty: true))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(label))
        {
            label = "WR";
        }

        var mapName = candidate.Map ?? mapInput;
        demoDates.TryGetValue(candidate.DemoId, out var date);
        var resolved = ResolveUserIdentity(candidate, player, demoUsers);
        entry = new WrHistoryEntry(player, detectedClass, mapName, label.ToUpperInvariant(), "MapRecord", time, null,
            split, improvement, false, date, candidate.DemoId, resolved.Identity?.SteamId64, resolved.Identity?.SteamId,
            resolved.SteamCandidates);
        return true;
    }

    private static bool TryParseBonusRecord(ChatCandidate candidate, string mapInput,
        IReadOnlyDictionary<ulong, DateTime?> demoDates, IReadOnlyDictionary<ulong, DemoUsers> demoUsers,
        out WrHistoryEntry? entry)
    {
        entry = null;
        var match = BonusRecordRegex.Match(candidate.Text);
        if (!match.Success)
        {
            return false;
        }

        var detectedClass = NormalizeClass(match.Groups["class"].Value);
        var player = match.Groups["player"].Value.Trim();
        var time = NormalizeTime(match.Groups["time"].Value);
        if (!IsValidRecordTime(time))
        {
            return false;
        }
        var split = NormalizeSignedTime(match.Groups["split"].Value);
        var improvement = match.Groups["improvement"].Success
            ? NormalizeTime(match.Groups["improvement"].Value)
            : null;
        var label = match.Groups["label"].Value;
        if (!IsWorldRecordLabel(label, allowEmpty: true))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(label))
        {
            label = "WR";
        }

        var mapName = candidate.Map ?? mapInput;
        var source = "Bonus " + match.Groups["index"].Value;
        demoDates.TryGetValue(candidate.DemoId, out var date);
        var resolved = ResolveUserIdentity(candidate, player, demoUsers);
        entry = new WrHistoryEntry(player, detectedClass, mapName, label.ToUpperInvariant(), source, time, null, split,
            improvement, false, date, candidate.DemoId, resolved.Identity?.SteamId64, resolved.Identity?.SteamId,
            resolved.SteamCandidates);
        return true;
    }

    private static bool TryParseSetBonusRecord(ChatCandidate candidate, string mapInput,
        IReadOnlyDictionary<ulong, DateTime?> demoDates, IReadOnlyDictionary<ulong, DemoUsers> demoUsers,
        out WrHistoryEntry? entry)
    {
        entry = null;
        var match = SetBonusRegex.Match(candidate.Text);
        if (!match.Success)
        {
            return false;
        }

        var detectedClass = NormalizeClass(match.Groups["class"].Value);
        var player = match.Groups["player"].Value.Trim();
        var time = NormalizeTime(match.Groups["time"].Value);
        if (!IsValidRecordTime(time))
        {
            return false;
        }
        var mapName = candidate.Map ?? mapInput;
        var source = "Bonus " + match.Groups["index"].Value + " First";

        demoDates.TryGetValue(candidate.DemoId, out var date);
        var resolved = ResolveUserIdentity(candidate, player, demoUsers);
        entry = new WrHistoryEntry(player, detectedClass, mapName, "WR", source, time, null, null, null, false, date,
            candidate.DemoId, resolved.Identity?.SteamId64, resolved.Identity?.SteamId, resolved.SteamCandidates);
        return true;
    }

    private static bool TryParseCourseRecord(ChatCandidate candidate, string mapInput,
        IReadOnlyDictionary<ulong, DateTime?> demoDates, IReadOnlyDictionary<ulong, DemoUsers> demoUsers,
        out WrHistoryEntry? entry)
    {
        entry = null;
        var match = CourseRecordRegex.Match(candidate.Text);
        if (!match.Success)
        {
            return false;
        }

        var detectedClass = NormalizeClass(match.Groups["class"].Value);
        var player = match.Groups["player"].Value.Trim();
        var time = NormalizeTime(match.Groups["time"].Value);
        if (!IsValidRecordTime(time))
        {
            return false;
        }
        var split = NormalizeSignedTime(match.Groups["split"].Value);
        var improvement = match.Groups["improvement"].Success
            ? NormalizeTime(match.Groups["improvement"].Value)
            : null;
        var label = match.Groups["label"].Value;
        if (!IsWorldRecordLabel(label, allowEmpty: true))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(label))
        {
            label = "WR";
        }

        var mapName = candidate.Map ?? mapInput;
        var source = "Course " + match.Groups["index"].Value;
        demoDates.TryGetValue(candidate.DemoId, out var date);
        var resolved = ResolveUserIdentity(candidate, player, demoUsers);
        entry = new WrHistoryEntry(player, detectedClass, mapName, label.ToUpperInvariant(), source, time, null, split,
            improvement, false, date, candidate.DemoId, resolved.Identity?.SteamId64, resolved.Identity?.SteamId,
            resolved.SteamCandidates);
        return true;
    }

    private static bool TryParseSetCourseRecord(ChatCandidate candidate, string mapInput,
        IReadOnlyDictionary<ulong, DateTime?> demoDates, IReadOnlyDictionary<ulong, DemoUsers> demoUsers,
        out WrHistoryEntry? entry)
    {
        entry = null;
        var match = SetCourseRegex.Match(candidate.Text);
        if (!match.Success)
        {
            return false;
        }

        var detectedClass = NormalizeClass(match.Groups["class"].Value);
        var player = match.Groups["player"].Value.Trim();
        var time = NormalizeTime(match.Groups["time"].Value);
        if (!IsValidRecordTime(time))
        {
            return false;
        }
        var mapName = candidate.Map ?? mapInput;
        var source = "Course " + match.Groups["index"].Value + " First";

        demoDates.TryGetValue(candidate.DemoId, out var date);
        var resolved = ResolveUserIdentity(candidate, player, demoUsers);
        entry = new WrHistoryEntry(player, detectedClass, mapName, "WR", source, time, null, null, null, false, date,
            candidate.DemoId, resolved.Identity?.SteamId64, resolved.Identity?.SteamId, resolved.SteamCandidates);
        return true;
    }

    private static bool TryParseSetCourseSegmentRecord(ChatCandidate candidate, string mapInput,
        IReadOnlyDictionary<ulong, DateTime?> demoDates, IReadOnlyDictionary<ulong, DemoUsers> demoUsers,
        out WrHistoryEntry? entry)
    {
        entry = null;
        var match = SetCourseSegmentRegex.Match(candidate.Text);
        if (!match.Success)
        {
            return false;
        }

        var detectedClass = NormalizeClass(match.Groups["class"].Value);
        var player = match.Groups["player"].Value.Trim();
        var time = NormalizeTime(match.Groups["time"].Value);
        if (!IsValidRecordTime(time))
        {
            return false;
        }
        var segment = "C" + match.Groups["index"].Value + " - " + match.Groups["name"].Value.Trim();
        var mapName = candidate.Map ?? mapInput;

        demoDates.TryGetValue(candidate.DemoId, out var date);
        var resolved = ResolveUserIdentity(candidate, player, demoUsers);
        entry = new WrHistoryEntry(player, detectedClass, mapName, "WR", segment + " First", time, null, null, null,
            false, date, candidate.DemoId, resolved.Identity?.SteamId64, resolved.Identity?.SteamId,
            resolved.SteamCandidates);
        return true;
    }

    private static bool TryParseCourseSegmentRecord(ChatCandidate candidate, string mapInput,
        IReadOnlyDictionary<ulong, DateTime?> demoDates, IReadOnlyDictionary<ulong, DemoUsers> demoUsers,
        out WrHistoryEntry? entry)
    {
        entry = null;
        var match = CourseSegmentRecordRegex.Match(candidate.Text);
        if (!match.Success)
        {
            return false;
        }

        var detectedClass = NormalizeClass(match.Groups["class"].Value);
        var player = match.Groups["player"].Value.Trim();
        var time = NormalizeTime(match.Groups["time"].Value);
        if (!IsValidRecordTime(time))
        {
            return false;
        }
        var split = NormalizeSignedTime(match.Groups["split"].Value);
        var improvement = match.Groups["improvement"].Success
            ? NormalizeTime(match.Groups["improvement"].Value)
            : null;
        var label = match.Groups["label"].Value;
        if (!IsWorldRecordLabel(label, allowEmpty: true))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(label))
        {
            label = "WR";
        }

        var segment = "C" + match.Groups["index"].Value + " - " + match.Groups["name"].Value.Trim();
        var mapName = candidate.Map ?? mapInput;
        demoDates.TryGetValue(candidate.DemoId, out var date);
        var resolved = ResolveUserIdentity(candidate, player, demoUsers);
        entry = new WrHistoryEntry(player, detectedClass, mapName, label.ToUpperInvariant(), segment, time, null, split,
            improvement, false, date, candidate.DemoId, resolved.Identity?.SteamId64, resolved.Identity?.SteamId,
            resolved.SteamCandidates);
        return true;
    }

    private static bool TryParseMapRun(ChatCandidate candidate, string mapInput,
        IReadOnlyDictionary<ulong, DateTime?> demoDates, IReadOnlyDictionary<ulong, DemoUsers> demoUsers,
        out WrHistoryEntry? entry)
    {
        entry = null;
        var match = MapRunRegex.Match(candidate.Text);
        if (!match.Success)
        {
            return false;
        }

        var detectedClass = NormalizeClass(match.Groups["class"].Value);
        var player = match.Groups["player"].Value.Trim();
        var runTime = NormalizeTime(match.Groups["run"].Value);
        var splitRaw = match.Groups["split"].Value;
        var split = NormalizeSignedTime(splitRaw);
        var improvement = match.Groups["improvement"].Success
            ? NormalizeTime(match.Groups["improvement"].Value)
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

        if (!IsValidRecordTime(recordTime))
        {
            return false;
        }

        var mapName = candidate.Map ?? mapInput;
        demoDates.TryGetValue(candidate.DemoId, out var date);
        var resolved = ResolveUserIdentity(candidate, player, demoUsers);
        entry = new WrHistoryEntry(player, detectedClass, mapName, "WR", "MapRun", recordTime,
            runTime, split, improvement, inferred, date, candidate.DemoId, resolved.Identity?.SteamId64,
            resolved.Identity?.SteamId, resolved.SteamCandidates);
        return true;
    }

    private static bool TryParseRankedRecord(ChatCandidate candidate, string mapInput,
        IReadOnlyDictionary<ulong, DateTime?> demoDates, IReadOnlyDictionary<ulong, DemoUsers> demoUsers,
        out WrHistoryEntry? entry)
    {
        entry = null;
        var match = RankedTimeRegex.Match(candidate.Text);
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
        var time = NormalizeTime(match.Groups["time"].Value);
        if (!IsValidRecordTime(time))
        {
            return false;
        }
        var mapName = match.Groups["map"].Value;
        var segment = match.Groups["segment"].Value;
        var index = match.Groups["index"].Value;
        var source = "Ranked";
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
        entry = new WrHistoryEntry(player, detectedClass, mapName, "WR", source, time, null, null, null, true, date,
            candidate.DemoId, resolved.Identity?.SteamId64, resolved.Identity?.SteamId, resolved.SteamCandidates,
            IsLookup: true);
        return true;
    }

    internal static ResolvedIdentity ResolveUserIdentity(ChatCandidate candidate, string player,
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

    internal static bool IsSubRecordSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        return source.StartsWith("Bonus", StringComparison.OrdinalIgnoreCase)
               || source.StartsWith("Course", StringComparison.OrdinalIgnoreCase)
               || source.StartsWith("C", StringComparison.OrdinalIgnoreCase) && source.Length > 1
               || source.StartsWith("Ranked", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetHistoryBucket(WrHistoryEntry entry)
    {
        var source = entry.Source?.Trim();
        if (string.IsNullOrWhiteSpace(source))
        {
            return "Map";
        }

        if (source.StartsWith("Ranked", StringComparison.OrdinalIgnoreCase))
        {
            return "Map";
        }

        if (!IsSubRecordSource(source))
        {
            return "Map";
        }

        if (source.EndsWith(" First", StringComparison.OrdinalIgnoreCase))
        {
            source = source.Substring(0, source.Length - " First".Length).TrimEnd();
        }

        return source;
    }

    private static bool GetIncludeSubRecords()
    {
        return EnvVar.GetBool("TEMPUS_WR_INCLUDE_SUBRECORDS");
    }

    private static bool GetIncludeAllEntries()
    {
        return EnvVar.GetBool("TEMPUS_WR_INCLUDE_ALL");
    }

    private static bool GetIncludeInferred()
    {
        return EnvVar.GetBool("TEMPUS_WR_INCLUDE_INFERRED");
    }

    private static bool GetIncludeLookup()
    {
        return EnvVar.GetBool("TEMPUS_WR_INCLUDE_LOOKUP");
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

        return value;
    }

    private static string NormalizeTime(string value)
    {
        return TempusTime.NormalizeTime(value);
    }

    private static string NormalizeSignedTime(string value)
    {
        return TempusTime.NormalizeSignedTime(value);
    }

    private static string SplitMapSource(string rawMap, out string source)
    {
        source = "Compact";
        if (string.IsNullOrWhiteSpace(rawMap))
        {
            return rawMap;
        }

        var match = Regex.Match(rawMap,
            @"^(?<map>[^/]+)/(?<type>Bonus|Course)\s*#?(?<index>\d+)?$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
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

        if (!TryParseTimeCentiseconds(runTime, out var runCentiseconds))
        {
            return false;
        }

        if (!TryParseSignedTimeCentiseconds(splitRaw, out var splitCentiseconds, out var sign))
        {
            return false;
        }

        if (sign == 0)
        {
            return false;
        }

        if (sign < 0)
        {
            recordTime = FormatTimeFromCentiseconds(runCentiseconds);
            inferred = false;
            return true;
        }

        var inferredCentiseconds = runCentiseconds - splitCentiseconds;
        if (inferredCentiseconds <= 0)
        {
            return false;
        }

        recordTime = FormatTimeFromCentiseconds(inferredCentiseconds);
        inferred = true;
        return true;
    }

    private static bool IsWorldRecordLabel(string label, bool allowEmpty)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return allowEmpty;
        }

        return string.Equals(label, "WR", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidRecordTime(string value)
    {
        return TempusTime.TryParseTimeCentiseconds(value, out var centiseconds) && centiseconds > 0;
    }

    private static bool TryParseTimeCentiseconds(string value, out long centiseconds)
    {
        centiseconds = 0;
        if (!TempusTime.TryParseTimeCentiseconds(value, out var parsed))
        {
            return false;
        }

        centiseconds = parsed;
        return true;
    }

    private static bool TryParseSignedTimeCentiseconds(string value, out long centiseconds, out int sign)
    {
        centiseconds = 0;
        sign = 0;
        if (!TempusTime.TryParseSignedTimeCentiseconds(value, out var parsed, out sign))
        {
            return false;
        }

        centiseconds = parsed;
        return true;
    }

    private static string FormatTimeFromCentiseconds(long centiseconds)
    {
        if (centiseconds <= 0)
        {
            return TempusTime.FormatTimeFromCentiseconds(0);
        }

        var safe = centiseconds > int.MaxValue ? int.MaxValue : (int)centiseconds;
        return TempusTime.FormatTimeFromCentiseconds(safe);
    }

    internal static IEnumerable<WrHistoryEntry> BuildWrHistory(IEnumerable<WrHistoryEntry> entries, bool includeAll)
    {
        var ordered = entries
            .Where(entry => entry.Date != null)
            .Select(entry => new
            {
                Entry = entry,
                Centiseconds = TryParseTimeCentiseconds(entry.RecordTime, out var centiseconds)
                    ? centiseconds
                    : (long?)null,
                Priority = GetSourcePriority(entry)
            })
            .Where(entry => entry.Centiseconds != null)
            .OrderBy(entry => entry.Entry.Date)
            .ThenBy(entry => entry.Centiseconds)
            .ThenBy(entry => entry.Priority)
            .Select(entry => entry.Entry)
            .ToList();

        var history = new List<WrHistoryEntry>();
        var bestByBucket = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in ordered)
        {
            if (!TryParseTimeCentiseconds(entry.RecordTime, out var centiseconds))
            {
                continue;
            }

            var bucket = GetHistoryBucket(entry);
            var hasBest = bestByBucket.TryGetValue(bucket, out var best);
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

        return history;
    }

    private static int GetSourcePriority(WrHistoryEntry entry)
    {
        if (entry.Inferred)
        {
            return 100;
        }

        if (string.Equals(entry.Source, "MapRecord", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (string.Equals(entry.Source, "FirstRecord", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (string.Equals(entry.Source, "Compact", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (entry.Source.StartsWith("IRC", StringComparison.OrdinalIgnoreCase))
        {
            return 4;
        }

        if (string.Equals(entry.Source, "MapRun", StringComparison.OrdinalIgnoreCase))
        {
            return 5;
        }

        if (string.Equals(entry.Source, "Ranked", StringComparison.OrdinalIgnoreCase))
        {
            return 6;
        }

        return 50;
    }

    private static string WriteCsv(string map, string @class, IReadOnlyList<WrHistoryEntry> entries)
    {
        var fileName = ArchiveUtils.ToValidFileName($"wr_history_{map}_{@class}.csv");
        var filePath = Path.Combine(ArchivePath.TempRoot, fileName);
        var lines = new List<string>
        {
            "date,record_time,player,map,record_type,source,run_time,split,improvement,inferred,demo_id,steam_id64,steam_id,steam_candidates"
        };

        foreach (var entry in entries)
        {
            var date = ArchiveUtils.FormatDate(entry.Date);
            lines.Add(string.Join(',', new[]
            {
                date,
                entry.RecordTime,
                entry.Player.Replace(',', ' '),
                entry.Map.Replace(',', ' '),
                entry.RecordType,
                entry.Source.Replace(',', ' '),
                entry.RunTime ?? string.Empty,
                entry.Split ?? string.Empty,
                entry.Improvement ?? string.Empty,
                entry.Inferred ? "true" : "false",
                entry.DemoId?.ToString() ?? string.Empty,
                entry.SteamId64?.ToString() ?? string.Empty,
                entry.SteamId ?? string.Empty,
                entry.SteamCandidates ?? string.Empty
            }));
        }

        File.WriteAllLines(filePath, lines);
        return filePath;
    }

    internal static async Task<Dictionary<ulong, DateTime?>> LoadDemoDatesAsync(ArchiveDbContext db,
        IReadOnlyList<ulong> demoIds, CancellationToken cancellationToken)
    {
        if (demoIds.Count == 0)
        {
            return new Dictionary<ulong, DateTime?>();
        }

        var demoDates = new List<(ulong Id, double Date)>();
        foreach (var chunk in DbChunk.Chunk(demoIds))
        {
            var chunkDates = await db.Demos
                .AsNoTracking()
                .Where(x => chunk.Contains(x.Id))
                .Select(x => new { x.Id, x.Date })
                .ToListAsync(cancellationToken);
            demoDates.AddRange(chunkDates.Select(x => (x.Id, x.Date)));
        }

        return demoDates.ToDictionary(x => x.Id, x => (DateTime?)ArchiveUtils.GetDateFromTimestamp(x.Date));
    }

    internal static async Task<Dictionary<ulong, DemoUsers>> LoadDemoUsersAsync(ArchiveDbContext db,
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

    internal static Dictionary<ulong, DemoUsers> BuildDemoUsers(IEnumerable<StvUser> users)
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
}

public record WrHistoryEntry(string Player, string Class, string Map, string RecordType, string Source,
    string RecordTime, string? RunTime, string? Split, string? Improvement, bool Inferred, DateTime? Date = null,
    ulong? DemoId = null, long? SteamId64 = null, string? SteamId = null, string? SteamCandidates = null,
    bool IsLookup = false);

public record UserIdentity(long? SteamId64, string? SteamId);

internal sealed record ResolvedIdentity(UserIdentity? Identity, string? SteamCandidates);

internal sealed record UserIdentityCandidate(string Name, long? SteamId64, string? SteamId);

internal sealed class UserIdentityCandidateComparer : IEqualityComparer<UserIdentityCandidate>
{
    public static readonly UserIdentityCandidateComparer Instance = new();

    public bool Equals(UserIdentityCandidate? x, UserIdentityCandidate? y)
    {
        if (ReferenceEquals(x, y))
        {
            return true;
        }

        if (x is null || y is null)
        {
            return false;
        }

        return string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase)
               && x.SteamId64 == y.SteamId64
               && string.Equals(x.SteamId, y.SteamId, StringComparison.OrdinalIgnoreCase);
    }

    public int GetHashCode(UserIdentityCandidate obj)
    {
        return HashCode.Combine(obj.Name.ToUpperInvariant(), obj.SteamId64,
            obj.SteamId?.ToUpperInvariant());
    }
}
