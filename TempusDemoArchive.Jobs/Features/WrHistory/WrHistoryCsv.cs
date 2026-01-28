namespace TempusDemoArchive.Jobs;

internal static class WrHistoryCsv
{
    private static readonly string[] Header =
    {
        "date",
        "record_time",
        "player",
        "map",
        "record_type",
        "segment",
        "evidence",
        "evidence_source",
        "run_time",
        "split",
        "improvement",
        "demo_id",
        "steam_id64",
        "steam_id",
        "steam_candidates"
    };

    public static string Write(string outputRoot, string map, string @class, IReadOnlyList<WrHistoryEntry> entries,
        CancellationToken cancellationToken = default)
    {
        var fileName = ArchiveUtils.ToValidFileName($"wr_history_{map}_{@class}.csv");
        var filePath = Path.Combine(outputRoot, fileName);

        CsvOutput.Write(filePath, Header,
            entries.Select(entry => new string?[]
            {
                ArchiveUtils.FormatDate(entry.Date),
                entry.RecordTime,
                entry.Player,
                entry.Map,
                entry.RecordType,
                WrHistoryChat.GetSegment(entry),
                WrHistoryChat.GetEvidenceKind(entry),
                WrHistoryChat.GetEvidenceSource(entry),
                entry.RunTime,
                entry.Split,
                entry.Improvement,
                WrHistoryChat.ShouldIncludeDemoLink(entry) ? entry.DemoId?.ToString() : null,
                entry.SteamId64?.ToString(),
                entry.SteamId,
                entry.SteamCandidates
            }),
            cancellationToken);

        return filePath;
    }
}
