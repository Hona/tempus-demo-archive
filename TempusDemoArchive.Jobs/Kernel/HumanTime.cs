using System.Globalization;

namespace TempusDemoArchive.Jobs;

internal static class HumanTime
{
    public static string FormatHours(double seconds)
    {
        return (seconds / 3600d).ToString("0.##", CultureInfo.InvariantCulture) + "h";
    }
}
