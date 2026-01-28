namespace TempusDemoArchive.Jobs;

public class ExportUrlChatLogsJob : IJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine("Input demo urls separated by commas");
        var input = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(input))
        {
            Console.WriteLine("No URLs provided.");
            return;
        }

        var urls = input.Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        await using var db = new ArchiveDbContext();

        var rows = new List<string[]>();

        foreach (var url in urls)
        {
            var demo = await db.Demos
                .AsNoTracking()
                .Where(x => x.Url == url && x.Stv != null)
                .Select(x => new { x.Id, x.Date, x.Stv!.Header.Map })
                .FirstOrDefaultAsync(cancellationToken: cancellationToken);

            if (demo == null)
            {
                Console.WriteLine($"Demo not found for URL: {url}");
                continue;
            }

            var chats = await db.StvChats
                .AsNoTracking()
                .Where(x => x.DemoId == demo.Id)
                .OrderBy(x => x.Index)
                .ToListAsync(cancellationToken: cancellationToken);

            var date = ArchiveUtils.FormatDate(ArchiveUtils.GetDateFromTimestamp(demo.Date));

            foreach (var chat in chats)
            {
                rows.Add(new[]
                {
                    demo.Id.ToString(),
                    url,
                    date,
                    demo.Map ?? "unknown",
                    chat.Tick?.ToString() ?? "",
                    chat.From ?? "",
                    chat.Text ?? ""
                });
            }
        }

        if (rows.Count == 0)
        {
            Console.WriteLine("No chat logs found for the provided URLs.");
            return;
        }

        var fileName = $"url_chatlogs_{ArchiveUtils.ToValidFileName(DateTime.Now.ToString("s"))}.csv";
        var filePath = Path.Combine(ArchivePath.TempRoot, fileName);

        CsvOutput.Write(filePath,
            new[] { "DemoId", "Url", "Date", "Map", "Tick", "From", "Text" },
            rows,
            cancellationToken);

        Console.WriteLine($"Wrote {rows.Count} chat messages to {filePath}");
    }
}
