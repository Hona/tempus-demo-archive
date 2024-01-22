﻿using System.Text;
using System.Text.Json;

namespace TempusDemoArchive.Jobs;

public class FindExactMessage : IJob
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
            .Where(x => !x.Text.StartsWith("Tip |"))
            .ToList();

        if (matching.Count == 1)
        {
            var found = matching[0];
            
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

        if (matching.Count > 1)
        {
            var stringBuilder = new StringBuilder();
            
            stringBuilder.AppendLine("Multiple matches found");
            foreach (var match in matching)
            {
                var demo = await db.Demos.FirstOrDefaultAsync(x => x.Id == match.DemoId, cancellationToken: cancellationToken);
                stringBuilder.AppendLine(TESTINGWrHistoryJob.GetDateFromTimestamp(demo.Date) + ": " +match.Text);
            }

            stringBuilder.AppendLine("Total matches: " + matching.Count);

            var stringOutput = stringBuilder.ToString();
            Console.WriteLine(stringOutput);
            
            await File.WriteAllTextAsync(Path.Join(ArchivePath.TempRoot, 
                ToValidFileName("find_exact_message_"+DateTime.Now.ToString("s")
                                                     +"_"+foundMessage +".txt")), stringOutput, cancellationToken);
        }
    }
    
    public static string ToValidFileName(string name)
    {
        var invalidChars = System.IO.Path.GetInvalidFileNameChars();
        var validName = new string(name.Where(ch => !invalidChars.Contains(ch)).ToArray());

        // Optionally, you can replace invalid characters instead of removing them.
        // For example, replacing with an underscore:
        // var validName = string.Concat(name.Select(ch => invalidChars.Contains(ch) ? '_' : ch));

        return validName;
    }
}