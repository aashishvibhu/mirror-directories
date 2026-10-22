namespace DriveSync.Core;

/// <summary>
/// Helpers for catalog keys. A key is the source folder's name followed by the path inside it,
/// separated by backslashes, e.g. <c>Desktop\Taxes\2026.pdf</c>. Keys compare case-insensitively, like Windows paths.
/// </summary>
public static class KeyPath
{
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    public static string SourceName(string key)
    {
        int i = key.IndexOf('\\');
        return i < 0 ? key : key[..i];
    }

    /// <summary>The part of the key after the source name; empty for the source itself.</summary>
    public static string Relative(string key)
    {
        int i = key.IndexOf('\\');
        return i < 0 ? "" : key[(i + 1)..];
    }

    public static string Combine(string key, string child) => child.Length == 0 ? key : key + "\\" + child;

    /// <summary>True when <paramref name="key"/> is <paramref name="prefix"/> or lies inside it.</summary>
    public static bool IsUnder(string key, string prefix) =>
        key.Equals(prefix, StringComparison.OrdinalIgnoreCase)
        || (key.Length > prefix.Length && key[prefix.Length] == '\\' && key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
