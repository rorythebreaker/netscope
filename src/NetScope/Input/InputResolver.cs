using System.Text.RegularExpressions;
using NetScope.Diagnostics;

namespace NetScope.Input;

public enum InputKind { Path, Directory, List }

/// <summary>One input item in the order it appeared on the command line.</summary>
public sealed record InputItem(InputKind Kind, string Value);

public interface IInputResolver
{
    /// <summary>Expands files, masks, directories and list files into a de-duplicated, ordered list of full paths.</summary>
    IReadOnlyList<string> Resolve(IReadOnlyList<InputItem> items, bool recursive);
}

public sealed class InputResolver : IInputResolver
{
    private static readonly bool CaseInsensitiveFs = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
    private static readonly StringComparer PathComparer = CaseInsensitiveFs ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly char[] Wildcards = { '*', '?' };

    public IReadOnlyList<string> Resolve(IReadOnlyList<InputItem> items, bool recursive)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(PathComparer);

        void Add(string path)
        {
            var full = Path.GetFullPath(path);
            if (seen.Add(full)) result.Add(full);
        }

        foreach (var item in items)
        {
            switch (item.Kind)
            {
                case InputKind.Path:
                    foreach (var p in ExpandPattern(item.Value, Directory.GetCurrentDirectory())) Add(p);
                    break;
                case InputKind.Directory:
                    foreach (var p in EnumerateDirectory(item.Value, recursive)) Add(p);
                    break;
                case InputKind.List:
                    foreach (var p in ReadList(item.Value)) Add(p);
                    break;
            }
        }
        return result;
    }

    private static IEnumerable<string> EnumerateDirectory(string dir, bool recursive)
    {
        if (!Directory.Exists(dir))
        {
            Log.Skipped(dir, "directory not found");
            return Array.Empty<string>();
        }
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MatchCasing = MatchCasing.CaseInsensitive,
        };
        return Directory.EnumerateFiles(dir, "*", options)
            .Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Reads one path (or mask) per line. Relative paths are relative to the list file. Lines starting with '#' are comments.</summary>
    private static IEnumerable<string> ReadList(string listFile)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(listFile);
        }
        catch (Exception ex)
        {
            Log.Skipped(listFile, "cannot read input list: " + ex.Message);
            return Array.Empty<string>();
        }
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(listFile)) ?? Directory.GetCurrentDirectory();
        var list = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.Trim().Trim('"');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            list.AddRange(ExpandPattern(line, baseDir));
        }
        return list;
    }

    /// <summary>
    /// Expands a path that may contain '*' and '?' in any segment, plus '**' for any depth.
    /// A plain path is returned as-is (even if it does not exist: the loader reports it).
    /// </summary>
    internal static IEnumerable<string> ExpandPattern(string pattern, string baseDir)
    {
        if (pattern.IndexOfAny(Wildcards) < 0)
            return new[] { Path.IsPathRooted(pattern) ? pattern : Path.Combine(baseDir, pattern) };

        var normalized = pattern.Replace('/', Path.DirectorySeparatorChar);
        if (OperatingSystem.IsWindows()) normalized = normalized.Replace('\\', Path.DirectorySeparatorChar);
        if (!Path.IsPathRooted(normalized)) normalized = Path.Combine(baseDir, normalized);

        // Split into a literal root and the segments that contain wildcards.
        var root = Path.GetPathRoot(normalized) ?? "";
        var segments = normalized.Substring(root.Length).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        int firstWild = Array.FindIndex(segments, s => s.IndexOfAny(Wildcards) >= 0);
        var start = Path.Combine(new[] { root }.Concat(segments.Take(firstWild)).ToArray());
        var rest = segments.Skip(firstWild).ToArray();

        var matches = new List<string>();
        if (Directory.Exists(start)) Walk(start, rest, 0, matches);
        matches.Sort(StringComparer.Ordinal);
        if (matches.Count == 0) Log.Warning(pattern, "mask matched no files");
        return matches;
    }

    private static void Walk(string dir, string[] segments, int index, List<string> matches)
    {
        var segment = segments[index];
        bool last = index == segments.Length - 1;
        try
        {
            if (segment == "**")
            {
                // '**' matches zero or more directories.
                if (last)
                {
                    matches.AddRange(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories));
                    return;
                }
                Walk(dir, segments, index + 1, matches);
                foreach (var sub in Directory.EnumerateDirectories(dir))
                    Walk(sub, segments, index, matches);
                return;
            }

            var regex = GlobToRegex(segment);
            if (last)
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                    if (regex.IsMatch(Path.GetFileName(f))) matches.Add(f);
            }
            else
            {
                foreach (var d in Directory.EnumerateDirectories(dir))
                    if (regex.IsMatch(Path.GetFileName(d))) Walk(d, segments, index + 1, matches);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    internal static Regex GlobToRegex(string glob, bool ignoreCase = true)
    {
        var pattern = "^" + Regex.Escape(glob).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        var options = RegexOptions.CultureInvariant | RegexOptions.Singleline;
        if (ignoreCase) options |= RegexOptions.IgnoreCase;
        return new Regex(pattern, options);
    }
}
