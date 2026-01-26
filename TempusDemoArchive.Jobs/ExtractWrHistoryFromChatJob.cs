using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TempusDemoArchive.Persistence.Models.STVs;

namespace TempusDemoArchive.Jobs;

public class ExtractWrHistoryFromChatJob : IJob
{
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

    private readonly record struct ChatCandidate(ulong DemoId, string? Map, string Text, int? FromUserId);
    private sealed record DemoUsers(Dictionary<int, UserIdentity> ByUserId, Dictionary<string, UserIdentity?> ByName);

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

        var mapsMatching = await db.Stvs
            .AsNoTracking()
            .Where(x => x.Header.Map == map || EF.Functions.Like(x.Header.Map, mapPrefix + "%"))
            .Select(x => x.Header.Map)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(cancellationToken);

        foreach (var matchingMap in mapsMatching)
        {
            Console.WriteLine(matchingMap);
        }

        var suspectedWrMessages = await db.StvChats
            .AsNoTracking()
            .Where(chat => chat.Stv != null)
            .Where(chat => chat.Stv!.Header.Map == map
                           || EF.Functions.Like(chat.Stv.Header.Map, mapPrefix + "%"))
            .Where(chat => EF.Functions.Like(chat.Text, "Tempus | (%"))
            .Where(chat => EF.Functions.Like(chat.Text, "%map run%")
                           || EF.Functions.Like(chat.Text, "%beat the map record%")
                           || EF.Functions.Like(chat.Text, "%set the first map record%")
                           || EF.Functions.Like(chat.Text, "%is ranked%with time%")
                           || EF.Functions.Like(chat.Text, "%set Bonus%")
                           || EF.Functions.Like(chat.Text, "%set Course%")
                           || EF.Functions.Like(chat.Text, "%set C%")
                           || EF.Functions.Like(chat.Text, "%broke%Bonus%")
                           || EF.Functions.Like(chat.Text, "%broke%Course%")
                           || EF.Functions.Like(chat.Text, "%broke C%")
                           || EF.Functions.Like(chat.Text, "% WR)%")
                           || EF.Functions.Like(chat.Text, "% PR)%"))
            .Select(chat => new ChatCandidate(chat.DemoId, chat.Stv!.Header.Map, chat.Text, chat.FromUserId))
            .ToListAsync(cancellationToken);

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

    private static WrHistoryEntry? TryParseTempusRecord(ChatCandidate candidate, string mapInput,
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

    private static WrHistoryEntry? TryParseIrcRecord(ChatCandidate candidate, string map,
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
        if (!IsMapMatch(mapName, map))
        {
            return null;
        }

        var detectedClass = NormalizeClass(match.Groups["class"].Value);
        var player = match.Groups["player"].Value.Trim();
        var time = NormalizeTime(match.Groups["time"].Value);
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
        var identity = ResolveUserIdentity(candidate, player, demoUsers);
        var source = isSet ? "IRCSet" : "IRC";
        return new WrHistoryEntry(player, detectedClass, mapName, label.ToUpperInvariant(), source, time, null, split,
            null, false, date, candidate.DemoId, identity?.SteamId64, identity?.SteamId);
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
        var player = match.Groups["player"].Value.Trim();
        demoDates.TryGetValue(candidate.DemoId, out var date);
        var identity = ResolveUserIdentity(candidate, player, demoUsers);
        entry = new WrHistoryEntry(player, detectedClass, mapName, label, source, time, null, null, null, false, date,
            candidate.DemoId, identity?.SteamId64, identity?.SteamId);
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
        var mapName = candidate.Map ?? mapInput;

        demoDates.TryGetValue(candidate.DemoId, out var date);
        var identity = ResolveUserIdentity(candidate, player, demoUsers);
        entry = new WrHistoryEntry(player, detectedClass, mapName, "WR", "FirstRecord", time, null, null, null,
            false, date, candidate.DemoId, identity?.SteamId64, identity?.SteamId);
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
        var identity = ResolveUserIdentity(candidate, player, demoUsers);
        entry = new WrHistoryEntry(player, detectedClass, mapName, label.ToUpperInvariant(), "MapRecord", time, null,
            split, improvement, false, date, candidate.DemoId, identity?.SteamId64, identity?.SteamId);
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
        var identity = ResolveUserIdentity(candidate, player, demoUsers);
        entry = new WrHistoryEntry(player, detectedClass, mapName, label.ToUpperInvariant(), source, time, null, split,
            improvement, false, date, candidate.DemoId, identity?.SteamId64, identity?.SteamId);
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
        var mapName = candidate.Map ?? mapInput;
        var source = "Bonus " + match.Groups["index"].Value + " First";

        demoDates.TryGetValue(candidate.DemoId, out var date);
        var identity = ResolveUserIdentity(candidate, player, demoUsers);
        entry = new WrHistoryEntry(player, detectedClass, mapName, "WR", source, time, null, null, null, false, date,
            candidate.DemoId, identity?.SteamId64, identity?.SteamId);
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
        var identity = ResolveUserIdentity(candidate, player, demoUsers);
        entry = new WrHistoryEntry(player, detectedClass, mapName, label.ToUpperInvariant(), source, time, null, split,
            improvement, false, date, candidate.DemoId, identity?.SteamId64, identity?.SteamId);
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
        var mapName = candidate.Map ?? mapInput;
        var source = "Course " + match.Groups["index"].Value + " First";

        demoDates.TryGetValue(candidate.DemoId, out var date);
        var identity = ResolveUserIdentity(candidate, player, demoUsers);
        entry = new WrHistoryEntry(player, detectedClass, mapName, "WR", source, time, null, null, null, false, date,
            candidate.DemoId, identity?.SteamId64, identity?.SteamId);
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
        var segment = "C" + match.Groups["index"].Value + " - " + match.Groups["name"].Value.Trim();
        var mapName = candidate.Map ?? mapInput;

        demoDates.TryGetValue(candidate.DemoId, out var date);
        var identity = ResolveUserIdentity(candidate, player, demoUsers);
        entry = new WrHistoryEntry(player, detectedClass, mapName, "WR", segment + " First", time, null, null, null,
            false, date, candidate.DemoId, identity?.SteamId64, identity?.SteamId);
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
        var identity = ResolveUserIdentity(candidate, player, demoUsers);
        entry = new WrHistoryEntry(player, detectedClass, mapName, label.ToUpperInvariant(), segment, time, null, split,
            improvement, false, date, candidate.DemoId, identity?.SteamId64, identity?.SteamId);
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

        var mapName = candidate.Map ?? mapInput;
        demoDates.TryGetValue(candidate.DemoId, out var date);
        var identity = ResolveUserIdentity(candidate, player, demoUsers);
        entry = new WrHistoryEntry(player, detectedClass, mapName, "WR", "MapRun", recordTime,
            runTime, split, improvement, inferred, date, candidate.DemoId, identity?.SteamId64, identity?.SteamId);
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
        var identity = ResolveUserIdentity(candidate, player, demoUsers);
        entry = new WrHistoryEntry(player, detectedClass, mapName, "WR", source, time, null, null, null, true, date,
            candidate.DemoId, identity?.SteamId64, identity?.SteamId);
        return true;
    }

    private static UserIdentity? ResolveUserIdentity(ChatCandidate candidate, string player,
        IReadOnlyDictionary<ulong, DemoUsers> demoUsers)
    {
        if (!demoUsers.TryGetValue(candidate.DemoId, out var users))
        {
            return null;
        }

        if (candidate.FromUserId.HasValue
            && users.ByUserId.TryGetValue(candidate.FromUserId.Value, out var byUserId))
        {
            return byUserId;
        }

        if (string.IsNullOrWhiteSpace(player))
        {
            return null;
        }

        var normalized = player.Trim();
        if (users.ByName.TryGetValue(normalized, out var byName))
        {
            return byName;
        }

        if (normalized.EndsWith("...", StringComparison.Ordinal) && normalized.Length > 3)
        {
            var prefix = normalized.Substring(0, normalized.Length - 3).Trim();
            if (prefix.Length > 0)
            {
                var matches = users.ByName
                    .Where(entry => entry.Value != null
                                    && entry.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Select(entry => entry.Value)
                    .Distinct()
                    .ToList();

                if (matches.Count == 1)
                {
                    return matches[0];
                }
            }
        }

        return null;
    }

    private static bool IsMapMatch(string mapName, string mapInput)
    {
        return string.Equals(mapName, mapInput, StringComparison.OrdinalIgnoreCase)
               || mapName.StartsWith(mapInput + "_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSubRecordSource(string source)
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

    private static bool GetIncludeSubRecords()
    {
        var value = Environment.GetEnvironmentVariable("TEMPUS_WR_INCLUDE_SUBRECORDS");
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool GetIncludeAllEntries()
    {
        var value = Environment.GetEnvironmentVariable("TEMPUS_WR_INCLUDE_ALL");
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool GetIncludeInferred()
    {
        var value = Environment.GetEnvironmentVariable("TEMPUS_WR_INCLUDE_INFERRED");
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
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
        return TryParseTimeSeconds(value, out var seconds) ? FormatTime(seconds) : value;
    }

    private static string NormalizeSignedTime(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var sign = string.Empty;
        var raw = value;
        if (value.StartsWith("+", StringComparison.Ordinal) || value.StartsWith("-", StringComparison.Ordinal))
        {
            sign = value.Substring(0, 1);
            raw = value.Substring(1);
        }

        return TryParseTimeSeconds(raw, out var seconds)
            ? sign + FormatTime(seconds)
            : value;
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

        if (!TryParseTimeSeconds(runTime, out var runSeconds))
        {
            return false;
        }

        if (!TryParseSignedTimeSeconds(splitRaw, out var splitSeconds, out var sign))
        {
            return false;
        }

        if (sign == 0)
        {
            return false;
        }

        if (sign < 0)
        {
            recordTime = FormatTime(runSeconds);
            inferred = false;
            return true;
        }

        var inferredSeconds = runSeconds - splitSeconds;
        if (inferredSeconds <= 0)
        {
            return false;
        }

        recordTime = FormatTime(inferredSeconds);
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

    private static bool TryParseTimeSeconds(string value, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split(':');
        if (parts.Length is < 2 or > 3)
        {
            return false;
        }

        if (!double.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var secPart))
        {
            return false;
        }

        if (!int.TryParse(parts[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
        {
            return false;
        }

        var hours = 0;
        if (parts.Length == 3
            && !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out hours))
        {
            return false;
        }

        seconds = hours * 3600 + minutes * 60 + secPart;
        return true;
    }

    private static bool TryParseSignedTimeSeconds(string value, out double seconds, out int sign)
    {
        seconds = 0;
        sign = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var raw = value;
        if (value.StartsWith("+", StringComparison.Ordinal))
        {
            sign = 1;
            raw = value.Substring(1);
        }
        else if (value.StartsWith("-", StringComparison.Ordinal))
        {
            sign = -1;
            raw = value.Substring(1);
        }

        if (!TryParseTimeSeconds(raw, out seconds))
        {
            return false;
        }

        if (sign == 0)
        {
            sign = 0;
        }

        return true;
    }

    private static string FormatTime(double seconds)
    {
        var rounded = Math.Round(seconds, 2, MidpointRounding.AwayFromZero);
        if (rounded < 0)
        {
            rounded = 0;
        }

        var hours = (int)(rounded / 3600);
        var minutes = (int)((rounded % 3600) / 60);
        var secs = rounded % 60;
        var secondsText = secs.ToString("00.00", CultureInfo.InvariantCulture);
        if (hours > 0)
        {
            return $"{hours}:{minutes:D2}:{secondsText}";
        }

        return $"{minutes:D2}:{secondsText}";
    }

    private static IEnumerable<WrHistoryEntry> BuildWrHistory(IEnumerable<WrHistoryEntry> entries, bool includeAll)
    {
        var ordered = entries
            .Where(entry => entry.Date != null)
            .Select(entry => new
            {
                Entry = entry,
                Seconds = TryParseTimeSeconds(entry.RecordTime, out var seconds) ? seconds : (double?)null,
                Priority = GetSourcePriority(entry)
            })
            .Where(entry => entry.Seconds != null)
            .OrderBy(entry => entry.Entry.Date)
            .ThenBy(entry => entry.Seconds)
            .ThenBy(entry => entry.Priority)
            .Select(entry => entry.Entry)
            .ToList();

        if (includeAll)
        {
            return ordered;
        }

        var history = new List<WrHistoryEntry>();
        double? best = null;
        foreach (var entry in ordered)
        {
            if (!TryParseTimeSeconds(entry.RecordTime, out var seconds))
            {
                continue;
            }

            if (best == null || seconds < best.Value - 0.0001)
            {
                history.Add(entry);
                best = seconds;
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
            "date,record_time,player,map,record_type,source,run_time,split,improvement,inferred,demo_id,steam_id64,steam_id"
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
                entry.SteamId ?? string.Empty
            }));
        }

        File.WriteAllLines(filePath, lines);
        return filePath;
    }

    private static async Task<Dictionary<ulong, DateTime?>> LoadDemoDatesAsync(ArchiveDbContext db,
        IReadOnlyList<ulong> demoIds, CancellationToken cancellationToken)
    {
        if (demoIds.Count == 0)
        {
            return new Dictionary<ulong, DateTime?>();
        }

        var demoDates = await db.Demos
            .AsNoTracking()
            .Where(x => demoIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Date })
            .ToListAsync(cancellationToken);

        return demoDates.ToDictionary(x => x.Id, x => (DateTime?)ArchiveUtils.GetDateFromTimestamp(x.Date));
    }

    private static async Task<Dictionary<ulong, DemoUsers>> LoadDemoUsersAsync(ArchiveDbContext db,
        IReadOnlyList<ulong> demoIds, CancellationToken cancellationToken)
    {
        if (demoIds.Count == 0)
        {
            return new Dictionary<ulong, DemoUsers>();
        }

        var users = await db.StvUsers
            .AsNoTracking()
            .Where(x => demoIds.Contains(x.DemoId))
            .ToListAsync(cancellationToken);

        return BuildDemoUsers(users);
    }

    private static Dictionary<ulong, DemoUsers> BuildDemoUsers(IEnumerable<StvUser> users)
    {
        var demoUsers = new Dictionary<ulong, DemoUsers>();
        foreach (var group in users.GroupBy(user => user.DemoId))
        {
            var byUserId = new Dictionary<int, UserIdentity>();
            var byName = new Dictionary<string, UserIdentity?>(StringComparer.OrdinalIgnoreCase);
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

                if (byName.TryGetValue(name, out var existing))
                {
                    if (existing != null)
                    {
                        byName[name] = null;
                    }
                }
                else
                {
                    byName[name] = identity;
                }
            }

            demoUsers[group.Key] = new DemoUsers(byUserId, byName);
        }

        return demoUsers;
    }
}

public record WrHistoryEntry(string Player, string Class, string Map, string RecordType, string Source,
    string RecordTime, string? RunTime, string? Split, string? Improvement, bool Inferred, DateTime? Date = null,
    ulong? DemoId = null, long? SteamId64 = null, string? SteamId = null);

public record UserIdentity(long? SteamId64, string? SteamId);
