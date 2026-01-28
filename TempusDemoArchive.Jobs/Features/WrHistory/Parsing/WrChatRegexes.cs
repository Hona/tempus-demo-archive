using System.Text.RegularExpressions;

namespace TempusDemoArchive.Jobs;

internal static class WrChatRegexes
{
    public static readonly Regex MapRecord = new(
        $@"^Tempus \| \((?<class>[^)]+)\) (?<player>.*?) beat the map record: (?<time>{TempusTime.TimePattern}) \((?:(?<label>WR|PR)\s*)?(?<split>{TempusTime.SignedTimePattern})\)(?: \| (?<improvement>{TempusTime.TimePattern}) improvement!?)?\!?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static readonly Regex MapRecordNoSplit = new(
        $@"^Tempus \| \((?<class>[^)]+)\) (?<player>.*?) beat the map record: (?<time>{TempusTime.TimePattern})\!?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static readonly Regex FirstMapRecord = new(
        $@"^Tempus \| \((?<class>[^)]+)\) (?<player>.*?) set the first map record: (?<time>{TempusTime.TimePattern})\!?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static readonly Regex BonusRecord = new(
        $@"^Tempus \| \((?<class>[^)]+)\) (?<player>.*?) broke\s+(?:~[^~]+~\s*)*Bonus\s*#?(?<index>\d+) (?<time>{TempusTime.TimePattern}) \((?:(?<label>WR|PR)\s*)?(?<split>{TempusTime.SignedTimePattern})\)(?: \| (?<improvement>{TempusTime.TimePattern}) improvement!?)?\!?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static readonly Regex CourseRecord = new(
        $@"^Tempus \| \((?<class>[^)]+)\) (?<player>.*?) broke\s+(?:~[^~]+~\s*)*Course\s*#?(?<index>\d+) (?<time>{TempusTime.TimePattern}) \((?:(?<label>WR|PR)\s*)?(?<split>{TempusTime.SignedTimePattern})\)(?: \| (?<improvement>{TempusTime.TimePattern}) improvement!?)?\!?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static readonly Regex CourseSegmentRecord = new(
        $@"^Tempus \| \((?<class>[^)]+)\) (?<player>.*?) broke\s+(?:~[^~]+~\s*)*C(?<index>\d+)\s*-\s*(?<name>.+?) (?<time>{TempusTime.TimePattern}) \((?:(?<label>WR|PR)\s*)?(?<split>{TempusTime.SignedTimePattern})\)(?: \| (?<improvement>{TempusTime.TimePattern}) improvement!?)?\!?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static readonly Regex SetBonus = new(
        $@"^Tempus \| \((?<class>[^)]+)\) (?<player>.*?) set Bonus\s*#?(?<index>\d+) (?<time>{TempusTime.TimePattern})\!?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static readonly Regex SetCourse = new(
        $@"^Tempus \| \((?<class>[^)]+)\) (?<player>.*?) set Course\s*#?(?<index>\d+) (?<time>{TempusTime.TimePattern})\!?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static readonly Regex SetCourseSegment = new(
        $@"^Tempus \| \((?<class>[^)]+)\) (?<player>.*?) set\s+(?:~[^~]+~\s*)*C(?<index>\d+)\s*-\s*(?<name>.+?) (?<time>{TempusTime.TimePattern})\!?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static readonly Regex RankedTime = new(
        $@"^Tempus \| \((?<class>[^)]+)\) (?<player>.*?) is ranked (?<rank>\d+)/(?<total>\d+) on (?<map>[^ ]+)(?:\s+(?<segment>Bonus|Course)\s+(?<index>\d+))? with time: (?<time>{TempusTime.TimePattern})\!?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static readonly Regex IrcWr = new(
        $@"^:: \((?<class>[^)]+)\) (?<player>.+?) broke (?<map>[^ ]+) WR: (?<time>{TempusTime.TimePattern}) \((?:(?<label>WR|PR)\s*)?(?<split>{TempusTime.SignedTimePattern})\)\!?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static readonly Regex IrcSet = new(
        $@"^:: \((?<class>[^)]+)\) (?<player>.+?) set (?<map>[^ ]+) WR: (?<time>{TempusTime.TimePattern})\!?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static readonly Regex CompactRecord = new(
        $@"^Tempus \| \((?<class>[^)]+?) (?<label>WR|PR)\) (?<map>.+?) :: (?<time>{TempusTime.TimePattern}) :: (?<player>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static readonly Regex CompactMapSource = new(
        @"^(?<map>[^/]+)/(?<type>Bonus|Course)\s*#?(?<index>\d+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static readonly Regex MapRun = new(
        $@"^Tempus \| \((?<class>[^)]+)\) (?<player>.*?) map run (?<run>{TempusTime.TimePattern}) \((?:(?<label>WR|PR)\s*)?(?<split>{TempusTime.SignedTimePattern})\)(?: \| (?<improvement>{TempusTime.TimePattern}) improvement!?)?\!?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
}
