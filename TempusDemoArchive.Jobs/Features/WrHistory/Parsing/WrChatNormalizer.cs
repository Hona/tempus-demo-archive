namespace TempusDemoArchive.Jobs;

internal static class WrChatNormalizer
{
    public static string NormalizeClass(string value)
    {
        if (string.Equals(value, "Soldier", StringComparison.OrdinalIgnoreCase))
        {
            return "Solly";
        }

        if (string.Equals(value, "Demoman", StringComparison.OrdinalIgnoreCase))
        {
            return "Demo";
        }

        return value.Trim();
    }
}
