namespace TempusDemoArchive.Jobs;

internal static class WrHistoryCsv
{
    private static readonly string[] Header =
    {
        WrHistoryConstants.CsvColumn.Date,
        WrHistoryConstants.CsvColumn.RecordTime,
        WrHistoryConstants.CsvColumn.Player,
        WrHistoryConstants.CsvColumn.Map,
        WrHistoryConstants.CsvColumn.RecordType,
        WrHistoryConstants.CsvColumn.Segment,
        WrHistoryConstants.CsvColumn.Evidence,
        WrHistoryConstants.CsvColumn.EvidenceSource,
        WrHistoryConstants.CsvColumn.RunTime,
        WrHistoryConstants.CsvColumn.Split,
        WrHistoryConstants.CsvColumn.Improvement,
        WrHistoryConstants.CsvColumn.DemoId,
        WrHistoryConstants.CsvColumn.SteamId64,
        WrHistoryConstants.CsvColumn.SteamId,
        WrHistoryConstants.CsvColumn.SteamCandidates
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
                // Only record-setting messages can be reliably linked to the record demo.
                WrHistoryChat.ShouldIncludeDemoLink(entry) ? entry.DemoId?.ToString() : null,
                entry.SteamId64?.ToString(),
                entry.SteamId,
                entry.SteamCandidates
            }),
            cancellationToken);

        return filePath;
    }
}
