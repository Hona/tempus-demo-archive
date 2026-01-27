using System.Text.RegularExpressions;

namespace TempusDemoArchive.Jobs;

internal static class WrChatRegexes
{
    public static readonly Regex MapRun = new(
        $@"^Tempus \| \((?<class>[^)]+)\) (?<player>.*?) map run (?<run>{TempusTime.TimePattern}) \((?:(?<label>WR|PR)\s*)?(?<split>{TempusTime.SignedTimePattern})\)(?: \| (?<improvement>{TempusTime.TimePattern}) improvement!?)?\!?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
}
