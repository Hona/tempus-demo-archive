using TempusDemoArchive.Jobs.StvProcessor;

namespace TempusDemoArchive.Jobs;

public record JobDefinition(string Id, string DisplayName, string Category, string Description, Func<IJob> Factory);

public static class JobCatalog
{
    public static readonly IReadOnlyList<JobDefinition> All = new List<JobDefinition>
    {
        new("ingest-archived-demos", "Ingest archived demos", "ingest",
            "Load demos from archived_demos.txt into the Demos table.", () => new IngestArchivedDemosJob()),
        new("crawl-record-demos", "Crawl record demos", "ingest",
            "Crawl Tempus records API to discover additional demo URLs.", () => new CrawlRecordDemosJob()),
        new("parse-demos", "Parse demos", "parse",
            "Download and parse unprocessed demos into Stv* tables.", () => new ParseDemosJob()),
        new("reparse-demos", "Reparse demos", "parse",
            "Reparse processed demos to refresh schema/fields.", () => new ReparseDemosJob()),
        new("print-stats", "Print archive stats", "utility",
            "Show processed/unprocessed counts and database size.", () => new PrintArchiveStatsJob()),
        new("fix-processed-flags", "Fix processed flags", "utility",
            "Set StvProcessed=true for demos that already have STV data.", () => new FixStvProcessedFlagsJob()),
        new("list-unprocessed", "List unprocessed demos", "utility",
            "Print all demos where StvProcessed is false.", () => new ListUnprocessedDemosJob()),
        new("export-map-chat", "Export map chat logs", "export",
            "Export all chat lines for a given map.", () => new ExportMapChatLogsJob()),
        new("find-map-names", "Find map names", "utility",
            "List distinct map names containing a substring.", () => new FindMapNamesJob()),
        new("search-server-wr", "Search server WR messages", "analysis",
            "Find WR-style messages for a server substring.", () => new SearchServerWrMessagesJob()),
        new("export-url-chat", "Export URL chat logs", "export",
            "Export chat logs for specific demo URLs.", () => new ExportUrlChatLogsJob()),
        new("search-chat", "Search chat messages", "analysis",
            "Find chat messages containing a phrase.", () => new SearchChatMessagesJob()),
        new("export-user-chat", "Export user chat logs", "export",
            "Export chat logs for a Steam ID/Steam64.", () => new ExportUserChatLogsJob()),
        new("rank-keyword", "Rank users by keyword", "analysis",
            "Rank users by occurrences of a keyword.", () => new RankUsersByKeywordJob()),
        new("sentiment", "Compute user sentiment", "analysis",
            "Compute sentiment per user from chat messages.", () => new ComputeUserSentimentJob()),
        new("sentiment-user", "Inspect user sentiment", "analysis",
            "Inspect sentiment for a specific user.", () => new InspectUserSentimentJob()),
        new("sentiment-known", "Filter known-player sentiment", "analysis",
            "Filter sentiment results to a known player list.", () => new FilterKnownPlayersSentimentJob()),
        new("wr-history", "Extract WR history", "analysis",
            "Parse WR history from chat messages.", () => new ExtractWrHistoryFromChatJob())
    };

    public static JobDefinition? Find(string id)
    {
        return All.FirstOrDefault(job => string.Equals(job.Id, id, StringComparison.OrdinalIgnoreCase));
    }
}
