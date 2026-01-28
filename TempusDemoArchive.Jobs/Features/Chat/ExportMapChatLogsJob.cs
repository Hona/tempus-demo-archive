namespace TempusDemoArchive.Jobs;

public class ExportMapChatLogsJob : IJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var mapName = JobPrompts.ReadMapName();
        if (mapName == null)
        {
            return;
        }

        await using var db = new ArchiveDbContext();
        var matchingStvs = await db.Stvs
            .Where(x => x.Header.Map == mapName)
            .OrderBy(x => x.DemoId)
            .ToListAsync(cancellationToken);

        var demoIds = matchingStvs.Select(x => x.DemoId).ToList();
        var demoDates = await ArchiveQueries.LoadDemoDatesByIdAsync(db, demoIds, cancellationToken);

        var rows = new List<string[]>();

        foreach (var stv in matchingStvs)
        {
            var stvChats = await db.StvChats
                .Where(x => x.DemoId == stv.DemoId)
                .OrderBy(x => x.Index)
                .ToListAsync(cancellationToken);

            foreach (var chat in stvChats)
            {
                var date = demoDates.TryGetValue(stv.DemoId, out var demoDate)
                    ? ArchiveUtils.FormatDate(demoDate)
                    : "unknown";

                rows.Add(new[]
                {
                    stv.DemoId.ToString(),
                    date,
                    stv.Header.Map ?? "unknown",
                    chat.Tick?.ToString() ?? "",
                    chat.From ?? "",
                    chat.Text ?? ""
                });
            }
        }

        if (rows.Count == 0)
        {
            Console.WriteLine("No chat logs found for this map.");
            return;
        }

        var fileName = $"{ArchiveUtils.ToValidFileName(mapName)}_chatlogs.csv";
        var filePath = Path.Combine(ArchivePath.TempRoot, fileName);

        CsvOutput.Write(filePath,
            new[] { "DemoId", "Date", "Map", "Tick", "From", "Text" },
            rows,
            cancellationToken);

        Console.WriteLine($"Wrote {rows.Count} chat messages to {filePath}");
    }
}
