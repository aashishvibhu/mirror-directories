using System.Globalization;

namespace DriveSync.Core;

/// <summary>Sizes and counts in the words the app shows to people.</summary>
public static class Formatting
{
    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 40 => (bytes / (double)(1L << 40)).ToString("0.00 'TB'", CultureInfo.CurrentCulture),
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0 'GB'", CultureInfo.CurrentCulture),
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.0 'MB'", CultureInfo.CurrentCulture),
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("0 'KB'", CultureInfo.CurrentCulture),
        1 => "1 byte",
        _ => $"{bytes} bytes",
    };

    public static string Files(int count) => count == 1 ? "1 file" : $"{count.ToString("N0", CultureInfo.CurrentCulture)} files";
}
