using System.Text;
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
        
        var matching = db.StvChats.Where(x => EF.Functions.Like(x.Text, $"%{foundMessage}%")) // instead of string.Contains we're using EF functions for case insensitivity
            .ToList();

        if (matching.Count == 1)
        {
            var found = matching[0];

            var stv = db.Stvs.FirstOrDefault(x => x.DemoId == found.DemoId);
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

            var demo = await db.Demos.FirstOrDefaultAsync(x => x.Id == found.DemoId, cancellationToken: cancellationToken);

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
            var stringBuilder = new StringBuilder();
            
            stringBuilder.AppendLine("Multiple matches found");
            foreach (var match in matching)
            {
                var demo = await db.Demos.FirstOrDefaultAsync(x => x.Id == match.DemoId, cancellationToken: cancellationToken);
                var date = demo == null ? "unknown" : ArchiveUtils.FormatDate(ArchiveUtils.GetDateFromTimestamp(demo.Date));
                stringBuilder.AppendLine(date + ": " + match.Text);
            }

            stringBuilder.AppendLine("Total matches: " + matching.Count);

            var stringOutput = stringBuilder.ToString();
            Console.WriteLine(stringOutput);
            
            await File.WriteAllTextAsync(Path.Join(ArchivePath.TempRoot,
                ArchiveUtils.ToValidFileName("find_exact_message_" + DateTime.Now.ToString("s")
                                                + "_" + foundMessage + ".txt")), stringOutput, cancellationToken);
        }
    }
}
