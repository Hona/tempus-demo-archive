using System.Diagnostics;
using System.Text.Json;

namespace TempusDemoArchive.Jobs.StvProcessor;

public static class StvParser
{
    public static StvParserResponse ExtractStvData(string fileName)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = ArchivePath.DemoParserExe,
            Arguments = fileName,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        var process = Process.Start(processStartInfo);
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        
        if (output is null)
        {
            throw new InvalidOperationException("STV was invalid, parsing failed.");
        }
        
        return JsonSerializer.Deserialize<StvParserResponse>(output) ?? throw new InvalidOperationException("STV was invalid, parsing failed. Error: " + output);
    }
}