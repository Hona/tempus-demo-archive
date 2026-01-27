using System.Globalization;

namespace TempusDemoArchive.Jobs;

internal static class EnvVar
{
    public static string? GetString(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public static bool GetBool(string name, bool defaultValue = false)
    {
        var value = GetString(name);
        if (value == null)
        {
            return defaultValue;
        }

        if (IsTruthy(value))
        {
            return true;
        }

        if (IsFalsy(value))
        {
            return false;
        }

        return defaultValue;
    }

    public static int GetInt(string name, int defaultValue, int min = int.MinValue, int max = int.MaxValue)
    {
        var value = GetString(name);
        if (value == null)
        {
            return defaultValue;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return defaultValue;
        }

        return Math.Clamp(parsed, min, max);
    }

    public static int GetPositiveInt(string name, int defaultValue, int max = int.MaxValue)
    {
        var value = GetString(name);
        if (value == null)
        {
            return defaultValue;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            return defaultValue;
        }

        return parsed > max ? max : parsed;
    }

    public static int GetNonNegativeInt(string name, int defaultValue, int max = int.MaxValue)
    {
        var value = GetString(name);
        if (value == null)
        {
            return defaultValue;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
        {
            return defaultValue;
        }

        return parsed > max ? max : parsed;
    }

    private static bool IsTruthy(string value)
    {
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "y", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFalsy(string value)
    {
        return string.Equals(value, "0", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "n", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "off", StringComparison.OrdinalIgnoreCase);
    }
}
