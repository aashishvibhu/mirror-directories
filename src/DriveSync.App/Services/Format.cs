using System.Globalization;

namespace DriveSync.App.Services;

/// <summary>Times and durations in plain words.</summary>
public static class Format
{
    public static string Ago(DateTime utc, DateTime? nowUtc = null)
    {
        var elapsed = (nowUtc ?? DateTime.UtcNow) - utc;
        if (elapsed < TimeSpan.FromMinutes(1))
            return "just now";
        if (elapsed < TimeSpan.FromHours(1))
            return Plural((int)elapsed.TotalMinutes, "minute") + " ago";
        if (elapsed < TimeSpan.FromHours(24))
            return Plural((int)elapsed.TotalHours, "hour") + " ago";
        if (elapsed < TimeSpan.FromHours(48))
            return "yesterday";
        if (elapsed < TimeSpan.FromDays(14))
            return Plural((int)elapsed.TotalDays, "day") + " ago";
        return "on " + utc.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture);
    }

    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1))
            return "less than a minute";
        if (span < TimeSpan.FromHours(1))
            return "about " + Plural((int)Math.Round(span.TotalMinutes), "minute");
        int hours = (int)span.TotalHours, minutes = span.Minutes;
        return minutes == 0 ? "about " + Plural(hours, "hour") : $"about {Plural(hours, "hour")} {Plural(minutes, "minute")}";
    }

    public static string Date(DateTime utc) => utc.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);

    static string Plural(int n, string unit) => n == 1 ? $"1 {unit}" : $"{n} {unit}s";
}
