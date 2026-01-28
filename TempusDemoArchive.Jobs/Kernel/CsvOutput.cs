using System.Globalization;
using System.Text;
using CsvHelper;

namespace TempusDemoArchive.Jobs;

internal static class CsvOutput
{
    public static void Write(string filePath, string[] header, IEnumerable<string?[]> rows,
        CancellationToken cancellationToken = default)
    {
        if (header.Length == 0)
        {
            throw new ArgumentException("CSV header must not be empty.", nameof(header));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? ".");

        using var stream = File.Open(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);

        foreach (var field in header)
        {
            csv.WriteField(field);
        }

        csv.NextRecord();

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var field in row)
            {
                csv.WriteField(field ?? string.Empty);
            }

            csv.NextRecord();
        }
    }
}
