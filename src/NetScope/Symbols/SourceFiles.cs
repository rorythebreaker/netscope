using System.Collections.Concurrent;

namespace NetScope.Symbols;

/// <summary>
/// Source text for --source: the document path recorded in the PDB, or a file with the same name (or the same
/// trailing path) in the --source-path directories. Files are read once; missing files are remembered.
/// </summary>
public sealed class SourceFiles
{
    private readonly IReadOnlyList<string> _searchPaths;
    private readonly ConcurrentDictionary<string, string[]?> _cache = new(StringComparer.Ordinal);

    public SourceFiles(IReadOnlyList<string> searchPaths) => _searchPaths = searchPaths;

    /// <summary>Lines startLine..endLine (1-based, inclusive) of the document, or null when it cannot be found.</summary>
    public IReadOnlyList<string>? Lines(string document, int startLine, int endLine)
    {
        var lines = _cache.GetOrAdd(document, Read);
        if (lines == null || startLine < 1 || startLine > lines.Length) return null;
        int end = Math.Min(Math.Max(endLine, startLine), lines.Length);
        return lines[(startLine - 1)..end];
    }

    private string[]? Read(string document)
    {
        foreach (var path in Candidates(document))
        {
            try
            {
                if (File.Exists(path)) return File.ReadAllLines(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Try the next candidate.
            }
        }
        return null;
    }

    private IEnumerable<string> Candidates(string document)
    {
        yield return document;
        // "/build/src/App/Program.cs" -> try "App/Program.cs", then "Program.cs" under each search path.
        var parts = document.Split(SymbolLoader.Separators, StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in _searchPaths)
            for (int skip = Math.Max(0, parts.Length - 3); skip < parts.Length; skip++)
                yield return Path.Combine(new[] { dir }.Concat(parts.Skip(skip)).ToArray());
    }
}
