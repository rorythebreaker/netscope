using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using ICSharpCode.Decompiler.DebugInfo;
using SequencePoint = ICSharpCode.Decompiler.DebugInfo.SequencePoint;

namespace NetScope.Symbols;

/// <summary>A sequence point: the source range of the IL that starts at <see cref="Offset"/>.</summary>
public sealed record SourcePoint(int Offset, string Document, int StartLine, int EndLine, int StartColumn, int EndColumn)
{
    /// <summary>File name without directories, as shown in listings ("Program.cs").</summary>
    /// <remarks>PDB paths keep the separators of the build machine, so both kinds are handled on every OS.</remarks>
    public string File => Document.Substring(Document.LastIndexOfAny(SymbolLoader.Separators) + 1);
    public string Label => StartLine == EndLine ? $"{File}:{StartLine}" : $"{File}:{StartLine}-{EndLine}";
}

/// <summary>
/// Portable PDB of one assembly: sequence points and local variable names. Also serves the decompiler
/// (<see cref="IDebugInfoProvider"/>) so that pseudocode uses the real names of locals.
/// Thread-safe for reads.
/// </summary>
public sealed class DebugSymbols : IDebugInfoProvider, IDisposable
{
    private readonly MetadataReaderProvider _provider;
    private readonly MetadataReader _pdb;
    private readonly ConcurrentDictionary<MethodDefinitionHandle, IReadOnlyList<SourcePoint>> _points = new();
    private readonly ConcurrentDictionary<MethodDefinitionHandle, IReadOnlyDictionary<int, string>> _locals = new();

    internal DebugSymbols(MetadataReaderProvider provider, string origin)
    {
        _provider = provider;
        _pdb = provider.GetMetadataReader();
        Origin = origin;
    }

    /// <summary>"embedded" or the path of the .pdb file.</summary>
    public string Origin { get; }

    /// <summary>Visible (non-hidden) sequence points of a method, by IL offset.</summary>
    public IReadOnlyList<SourcePoint> SequencePointsOf(MethodDefinitionHandle method) => _points.GetOrAdd(method, ReadPoints);

    /// <summary>The visible sequence point whose IL range contains the offset, if any.</summary>
    public SourcePoint? At(MethodDefinitionHandle method, int ilOffset)
    {
        SourcePoint? last = null;
        foreach (var p in SequencePointsOf(method))
        {
            if (p.Offset > ilOffset) break;
            last = p;
        }
        return last;
    }

    /// <summary>Local variable names by slot (null where the PDB has none, e.g. compiler temporaries).</summary>
    public string?[] LocalNames(MethodDefinitionHandle method, int slots)
    {
        var map = _locals.GetOrAdd(method, ReadLocals);
        var names = new string?[slots];
        foreach (var (slot, name) in map)
            if (slot >= 0 && slot < slots) names[slot] = name;
        return names;
    }

    private IReadOnlyList<SourcePoint> ReadPoints(MethodDefinitionHandle method)
    {
        var list = new List<SourcePoint>();
        try
        {
            var info = _pdb.GetMethodDebugInformation(method.ToDebugInformationHandle());
            foreach (var sp in info.GetSequencePoints())
            {
                if (sp.IsHidden || sp.Document.IsNil) continue;
                var doc = _pdb.GetString(_pdb.GetDocument(sp.Document).Name);
                list.Add(new SourcePoint(sp.Offset, doc, sp.StartLine, sp.EndLine, sp.StartColumn, sp.EndColumn));
            }
        }
        catch (BadImageFormatException)
        {
            // Corrupt debug information for one method: show it without source lines.
        }
        list.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        return list;
    }

    private IReadOnlyDictionary<int, string> ReadLocals(MethodDefinitionHandle method)
    {
        var map = new Dictionary<int, string>();
        try
        {
            foreach (var scopeHandle in _pdb.GetLocalScopes(method))
            {
                var scope = _pdb.GetLocalScope(scopeHandle);
                foreach (var vh in scope.GetLocalVariables())
                {
                    var v = _pdb.GetLocalVariable(vh);
                    if ((v.Attributes & LocalVariableAttributes.DebuggerHidden) != 0) continue;
                    var name = _pdb.GetString(v.Name);
                    // Scopes are in IL order; the first (outermost) name of a reused slot wins.
                    if (name.Length > 0) map.TryAdd(v.Index, name);
                }
            }
        }
        catch (BadImageFormatException) { }
        return map;
    }

    // ------------------------------------------------------------------ IDebugInfoProvider (decompiler)

    public string Description => "NetScope portable PDB reader (" + Origin + ")";

    public string SourceFileName => Origin;

    public IList<SequencePoint> GetSequencePoints(MethodDefinitionHandle method)
    {
        var points = SequencePointsOf(method);
        var result = new List<SequencePoint>(points.Count);
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            result.Add(new SequencePoint
            {
                Offset = p.Offset,
                EndOffset = i + 1 < points.Count ? points[i + 1].Offset : p.Offset + 1,
                StartLine = p.StartLine,
                StartColumn = p.StartColumn,
                EndLine = p.EndLine,
                EndColumn = p.EndColumn,
                DocumentUrl = p.Document,
            });
        }
        return result;
    }

    public IList<Variable> GetVariables(MethodDefinitionHandle method) =>
        _locals.GetOrAdd(method, ReadLocals).OrderBy(kv => kv.Key).Select(kv => new Variable(kv.Key, kv.Value)).ToList();

    public bool TryGetName(MethodDefinitionHandle method, int index, out string name)
    {
        if (_locals.GetOrAdd(method, ReadLocals).TryGetValue(index, out var n))
        {
            name = n;
            return true;
        }
        name = null!;
        return false;
    }

    public bool TryGetExtraTypeInfo(MethodDefinitionHandle method, int index, out PdbExtraTypeInfo extraTypeInfo)
    {
        extraTypeInfo = default;
        return false;
    }

    public void Dispose() => _provider.Dispose();
}
