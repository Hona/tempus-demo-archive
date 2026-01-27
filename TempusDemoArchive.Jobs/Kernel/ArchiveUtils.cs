namespace TempusDemoArchive.Jobs;

public static class ArchiveUtils
{
    public static string GetMessageBody(string text)
    {
        var marker = " : ";
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        return index >= 0 ? text[(index + marker.Length)..] : text;
    }

    public static DateTime GetDateFromTimestamp(double timestamp)
    {
        var milliseconds = (long)(timestamp * 1000);
        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).DateTime;
    }

    public static string FormatDate(DateTime? date)
    {
        return date?.ToString("yyyy-MM-dd") ?? "unknown";
    }

    public static string ToValidFileName(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        return new string(name.Where(ch => !invalidChars.Contains(ch)).ToArray());
    }
}
