using System.IO.Enumeration;
using DriveSync.Core.IO;

namespace DriveSync.Core.Scanning;

/// <summary>
/// Decides which files and folders DriveSync ignores. Patterns use * and ?.
/// A pattern without a backslash matches a file or folder name anywhere ("*.tmp", "node_modules");
/// a pattern with one matches the path inside the source ("Projects\*\bin").
/// </summary>
public sealed class ExclusionMatcher
{
    static readonly string[] AlwaysExcluded = ["*" + FsUtil.TempSuffix];

    readonly string[] _namePatterns;
    readonly string[] _pathPatterns;

    public ExclusionMatcher(IEnumerable<string> patterns)
    {
        var all = patterns.Concat(AlwaysExcluded)
            .Select(p => p.Trim().Replace('/', '\\').Trim('\\'))
            .Where(p => p.Length > 0)
            .ToList();
        _namePatterns = [.. all.Where(p => !p.Contains('\\'))];
        _pathPatterns = [.. all.Where(p => p.Contains('\\'))];
    }

    /// <summary>Checks one item during a scan, whose parent folders have already passed.</summary>
    public bool IsExcluded(string name, string relativePath)
    {
        foreach (string p in _namePatterns)
            if (Matches(p, name))
                return true;
        foreach (string p in _pathPatterns)
            if (Matches(p, relativePath))
                return true;
        return false;
    }

    /// <summary>Checks a catalog key, including every folder on its path.</summary>
    public bool IsExcludedKey(string key)
    {
        string relative = KeyPath.Relative(key);
        foreach (string segment in relative.Split('\\'))
            foreach (string p in _namePatterns)
                if (Matches(p, segment))
                    return true;
        foreach (string p in _pathPatterns)
            if (Matches(p, relative))
                return true;
        return false;
    }

    // The matcher treats '\' as an escape character, so path patterns are compared with '/' separators.
    static bool Matches(string pattern, string text) =>
        FileSystemName.MatchesSimpleExpression(pattern.Replace('\\', '/'), text.Replace('\\', '/'), ignoreCase: true);
}
