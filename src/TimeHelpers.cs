// TimeHelpers.cs - Time formatting and parsing utilities

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

public static class TimeHelpers
{
    public static string FormatDuration(TimeSpan ts)
    {
        if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;

        long totalSeconds = (long)Math.Round(ts.TotalSeconds);
        long weeks = totalSeconds / (7 * 24 * 3600); totalSeconds %= (7 * 24 * 3600);
        long days = totalSeconds / (24 * 3600); totalSeconds %= (24 * 3600);
        long hours = totalSeconds / 3600; totalSeconds %= 3600;
        long minutes = totalSeconds / 60; totalSeconds %= 60;
        long seconds = totalSeconds;

        var parts = new List<string>(5);
        if (weeks > 0) parts.Add(weeks + "w");
        if (days > 0) parts.Add(days + "d");
        if (hours > 0) parts.Add(hours + "h");
        if (minutes > 0) parts.Add(minutes + "m");
        if (seconds > 0 && parts.Count == 0) parts.Add(seconds + "s");

        if (parts.Count > 2) parts = parts.GetRange(0, 2);
        return string.Join(" ", parts);
    }

    // Accepts "90s", "15m", "1h", "2d", "1w", or composites like "1h30m"
    public static bool TryParseDuration(string text, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        if (string.IsNullOrEmpty(text)) return false;

        var m = Regex.Matches(text.Trim().ToLowerInvariant(), @"(\d+)\s*([smhdw])");
        if (m.Count == 0)
        {
            if (int.TryParse(text.Trim(), out var mins) && mins > 0)
            {
                result = TimeSpan.FromMinutes(mins);
                return true;
            }
            return false;
        }

        long totalSeconds = 0;
        foreach (Match part in m)
        {
            long val = long.Parse(part.Groups[1].Value);
            switch (part.Groups[2].Value)
            {
                case "s": totalSeconds += val; break;
                case "m": totalSeconds += val * 60; break;
                case "h": totalSeconds += val * 3600; break;
                case "d": totalSeconds += val * 86400; break;
                case "w": totalSeconds += val * 604800; break;
            }
        }

        if (totalSeconds <= 0) return false;
        result = TimeSpan.FromSeconds(totalSeconds);
        return true;
    }
}
