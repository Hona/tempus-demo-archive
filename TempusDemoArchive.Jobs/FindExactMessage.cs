using System.Text.Json;

namespace TempusDemoArchive.Jobs;

public class FindExactMessage : IJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        await using var db = new ArchiveDbContext();
        
        var foundMessage =
            "Tempus | (Solly) Tritibellum broke Bonus 2 00:07.05 (PR -00:00.16) | 00:00.16 improvement!";
        
        var found = db.StvChats.FirstOrDefault(x => x.Text.Contains(foundMessage));

        if (found is not null)
        {
            found.Stv = db.Stvs.FirstOrDefault(x => x.DemoId == found.DemoId);
            found.Stv.Chats = null;
            found.Stv.Demo = null;
            
            Console.WriteLine(JsonSerializer.Serialize(found, options: new JsonSerializerOptions
            {
                WriteIndented = true
            }));
            
            var demo = await db.Demos.FirstOrDefaultAsync(x => x.Id == found.DemoId, cancellationToken: cancellationToken);

            demo.Stv = null;
            Console.WriteLine(JsonSerializer.Serialize(demo, options: new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
    }
}