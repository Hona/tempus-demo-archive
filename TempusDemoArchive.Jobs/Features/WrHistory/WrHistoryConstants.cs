namespace TempusDemoArchive.Jobs;

internal static class WrHistoryConstants
{
    // NOTE: These constants are part of a data export contract (CSV schema + evidence semantics).
    // Prefer updating/adding constants over introducing ad-hoc string literals to keep the pipeline
    // (Jobs -> CSV -> wr-history UI) deterministic and grep-able.

    internal const string Unknown = "unknown";

    internal static class RecordType
    {
        internal const string Wr = "WR";
    }

    internal static class Label
    {
        internal const string Wr = "WR";
        internal const string Sr = "SR";
    }

    internal static class Source
    {
        internal const string MapRecord = "MapRecord";
        internal const string FirstRecord = "FirstRecord";
        internal const string MapRun = "MapRun";
        internal const string Compact = "Compact";
        internal const string Ranked = "Ranked";
        internal const string Irc = "IRC";
        internal const string IrcSet = "IRCSet";
        internal const string ObservedWr = "ObservedWR";
    }

    internal static class Segment
    {
        internal const string Map = "Map";
    }

    internal static class SegmentPrefix
    {
        internal const string Bonus = "Bonus";
        internal const string Course = "Course";
        internal const string CourseSegment = "C";
        internal const string FirstSuffix = " First";
    }

    internal static class EvidenceKind
    {
        internal const string Record = "record";
        internal const string Command = "command";
        internal const string Observed = "observed";
        internal const string Announcement = "announcement";
    }

    internal static class EvidenceSource
    {
        internal const string WrSplit = "wr_split";
        internal const string RankCommand = "rank_command";
        internal const string WrCommand = "wr_command";
        internal const string IrcSet = "irc_set";
        internal const string Irc = "irc";
        internal const string MapRecord = "map_record";
        internal const string FirstRecord = "first_record";
        internal const string MapRun = "map_run";
        internal const string ZoneFirst = "zone_first";
        internal const string ZoneRecord = "zone_record";
        internal const string Unknown = "unknown";
    }

    internal static class CsvColumn
    {
        internal const string Date = "date";
        internal const string RecordTime = "record_time";
        internal const string Player = "player";
        internal const string Map = "map";
        internal const string RecordType = "record_type";
        internal const string Segment = "segment";
        internal const string Evidence = "evidence";
        internal const string EvidenceSource = "evidence_source";
        internal const string RunTime = "run_time";
        internal const string Split = "split";
        internal const string Improvement = "improvement";
        internal const string DemoId = "demo_id";
        internal const string SteamId64 = "steam_id64";
        internal const string SteamId = "steam_id";
        internal const string SteamCandidates = "steam_candidates";
    }
}
