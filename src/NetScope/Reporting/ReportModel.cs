using NetScope.Asm;
using NetScope.Calls;
using NetScope.Catalog;
using NetScope.Cfg;
using NetScope.Loading;
using NetScope.Native;
using NetScope.Pseudo;

namespace NetScope.Reporting;

/// <summary>Which groups of data are present in the report.</summary>
public sealed record OutputGroups(bool Methods, bool Calls, bool Cfg, bool Pseudo, bool IL, bool Native, bool CallGraph)
{
    /// <summary>--fields: fields each method reads and writes.</summary>
    public bool Fields { get; init; }
    /// <summary>--strings: table of string literals per assembly.</summary>
    public bool Strings { get; init; }
}

public sealed class AssemblyReport
{
    public required LoadedAssembly Assembly { get; init; }
    public List<MethodReport> Methods { get; } = new();
    public List<string> Errors { get; } = new();
    /// <summary>--strings: literals of the reported methods by value (filled only for that group).</summary>
    public Dictionary<string, CrossRefs.StringLiteral> StringIndex { get; } = new(StringComparer.Ordinal);
}

public sealed record CallReport(CallSite Site, MethodModel? GroupTarget);

/// <summary>Lines of one source file covered by a piece of IL.</summary>
public sealed record SourceRange(string File, int StartLine, int EndLine)
{
    public string Label => StartLine == EndLine ? $"{File}:{StartLine}" : $"{File}:{StartLine}-{EndLine}";
}

public sealed class MethodReport
{
    public required MethodModel Method { get; init; }
    public int? CodeSize { get; set; }
    public List<CallReport>? Calls { get; set; }
    public IReadOnlyList<CallerInfo>? Callers { get; set; }
    public ControlFlowGraph? Cfg { get; set; }
    public PseudoResult? Pseudo { get; set; }
    public ILListing? IL { get; set; }
    /// <summary>ildasm-like operands by IL offset, for instructions listed inside CFG blocks.</summary>
    public Dictionary<int, string>? Operands { get; set; }

    public string Instruction(IL.ILInstruction ins)
    {
        var operand = Operands != null && Operands.TryGetValue(ins.Offset, out var o) ? o : "";
        return operand.Length == 0 ? ins.Name : $"{ins.Name,-10} {operand}";
    }
    public NativeCode? Native { get; set; }
    /// <summary>Source lines of each CFG block (by block index), when the PDB has them.</summary>
    public Dictionary<int, SourceRange>? BlockSources { get; set; }
    /// <summary>What selected the method with --uses, --find-string, --field-refs.</summary>
    public IReadOnlyList<CrossRefs.CrossReference>? Matches { get; set; }
    /// <summary>--fields: field accesses in IL order.</summary>
    public IReadOnlyList<CrossRefs.FieldAccess>? Fields { get; set; }
    public List<string> Errors { get; } = new();
}

public interface IReporter
{
    /// <summary>File extension for --out directories.</summary>
    string Extension { get; }
    string BeginDocument();
    string RenderAssembly(AssemblyReport report, bool first);
    string RenderCallGraph(IReadOnlyList<CallGraphEdge> edges, bool afterAssemblies);
    string EndDocument();
}
