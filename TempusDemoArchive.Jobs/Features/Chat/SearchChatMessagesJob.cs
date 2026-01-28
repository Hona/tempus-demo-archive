using System.Text.Json;
using TempusDemoArchive.Persistence.Models.STVs;

namespace TempusDemoArchive.Jobs;

public class SearchChatMessagesJob : IJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        await using var db = new ArchiveDbContext();

        Console.WriteLine("Input message: ");
        var foundMessage = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(foundMessage))
        {
            Console.WriteLine("No message provided");
            return;
        }

        var matching = await db.StvChats
            .AsNoTracking()
            .Where(x => EF.Functions.Like(x.Text, $"%{foundMessage}%"))
            .ToListAsync(cancellationToken);

        if (matching.Count == 1)
        {
            var found = matching[0];

            var stv = await db.Stvs
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.DemoId == found.DemoId, cancellationToken);
            if (stv != null)
            {
                stv.Chats = new List<StvChat>();
                stv.Demo = null;
                found.Stv = stv;
            }

            Console.WriteLine(JsonSerializer.Serialize(found, options: new JsonSerializerOptions
            {
                WriteIndented = true
            }));

            var demo = await db.Demos
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == found.DemoId, cancellationToken);

            if (demo != null)
            {
                demo.Stv = null;
                Console.WriteLine(JsonSerializer.Serialize(demo, options: new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
            }
        }

        if (matching.Count > 1)
        {
            var demoIds = matching.Select(x => x.DemoId).Distinct().ToList();
            var demoDates = await ArchiveQueries.LoadDemoDatesByIdAsync(db, demoIds, cancellationToken);

            var rows = matching.Select(match =>
            {
                var date = demoDates.TryGetValue(match.DemoId, out var demoDate)
                    ? ArchiveUtils.FormatDate(demoDate)
                    : "unknown";
                return new[]
                {
                    match.DemoId.ToString(),
                    date,
                    match.Tick?.ToString() ?? "",
                    match.From ?? "",
                    match.Text ?? ""
                };
            }).ToList();

            Console.WriteLine($"Multiple matches found: {matching.Count}");
            foreach (var row in rows.Take(10))
            {
                Console.WriteLine($"{row[1]}: {row[4]}");
            }
            if (rows.Count > 10)
            {
                Console.WriteLine($"... and {rows.Count - 10} more");
            }

            var fileName = ArchiveUtils.ToValidFileName($"find_exact_message_{DateTime.Now.ToString("s")}_{foundMessage}.csv");
            var filePath = Path.Combine(ArchivePath.TempRoot, fileName);

            CsvOutput.Write(filePath,
                new[] { "DemoId", "Date", "Tick", "From", "Text" },
                rows,
                cancellationToken);

            Console.WriteLine($"Wrote {rows.Count} matches to {filePath}");
        }
    }
}
