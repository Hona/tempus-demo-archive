using System.Globalization;

namespace TempusDemoArchive.Jobs;

internal static class TempusTime
{
    public const string TimePattern = @"\d{1,2}:\d{2}(?::\d{2})?\.\d{2}";
    public const string SignedTimePattern = @"[+-]?" + TimePattern;

    public static string NormalizeTime(string value)
    {
        return TryParseTimeCentiseconds(value, out var centiseconds)
            ? FormatTimeFromCentiseconds(centiseconds)
            : value;
    }

    public static string NormalizeSignedTime(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var raw = value.Trim();
        var sign = string.Empty;
        if (raw.StartsWith('+') || raw.StartsWith('-'))
        {
            sign = raw.Substring(0, 1);
            raw = raw.Substring(1);
        }

        return TryParseTimeCentiseconds(raw, out var centiseconds)
            ? sign + FormatTimeFromCentiseconds(centiseconds)
            : value;
    }

    public static bool TryParseTimeCentiseconds(string value, out int centiseconds)
    {
        centiseconds = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Trim().Split(':');
        if (parts.Length is < 2 or > 3)
        {
            return false;
        }

        var hours = 0;
        var minutesPartIndex = 0;
        if (parts.Length == 3)
        {
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out hours) || hours < 0)
            {
                return false;
            }

            minutesPartIndex = 1;
        }

        if (!int.TryParse(parts[minutesPartIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
            || minutes < 0)
        {
            return false;
        }

        var secondsParts = parts[minutesPartIndex + 1].Split('.');
        if (secondsParts.Length != 2)
        {
            return false;
        }

        if (!int.TryParse(secondsParts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            || seconds < 0)
        {
            return false;
        }

        if (!int.TryParse(secondsParts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var cs)
            || cs < 0 || cs > 99)
        {
            return false;
        }

        var totalSeconds = hours * 3600 + minutes * 60 + seconds;
        centiseconds = totalSeconds * 100 + cs;
        return true;
    }

    public static bool TryParseSignedTimeCentiseconds(string value, out int centiseconds, out int sign)
    {
        centiseconds = 0;
        sign = 0;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var raw = value.Trim();
        if (raw.StartsWith('+'))
        {
            sign = 1;
            raw = raw.Substring(1);
        }
        else if (raw.StartsWith('-'))
        {
            sign = -1;
            raw = raw.Substring(1);
        }
        else
        {
            return false;
        }

        return TryParseTimeCentiseconds(raw, out centiseconds);
    }

    public static string FormatTimeFromCentiseconds(int centiseconds)
    {
        var rounded = centiseconds < 0 ? 0 : centiseconds;
        var totalSeconds = rounded / 100;
        var cs = rounded % 100;
        var seconds = totalSeconds % 60;
        var totalMinutes = totalSeconds / 60;
        var minutes = totalMinutes % 60;
        var hours = totalMinutes / 60;

        if (hours > 0)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}.{3:00}", hours, minutes, seconds,
                cs);
        }

        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}.{2:00}", minutes, seconds, cs);
    }
}
