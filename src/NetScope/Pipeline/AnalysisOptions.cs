using NetScope.Input;
using NetScope.Reporting;
using NetScope.Sandboxing;

namespace NetScope.Pipeline;

public sealed class AnalysisOptions
{
    public required IReadOnlyList<InputItem> Inputs { get; init; }
    public bool Recursive { get; init; }
    public IReadOnlyList<string> RefPaths { get; init; } = Array.Empty<string>();
    public required OutputGroups Groups { get; init; }
    public bool AllowRun { get; init; }
    public bool Raw { get; init; }
    public IReadOnlyList<string> Filters { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Types { get; init; } = Array.Empty<string>();
    public string Format { get; init; } = "text";
    public string? Out { get; init; }
    public bool Color { get; init; }
    public int Threads { get; init; } = Environment.ProcessorCount;
    public TimeSpan DecompileTimeout { get; init; } = TimeSpan.FromSeconds(20);
    /// <summary>After this many decompilations time out, the remaining methods get the simplified pseudocode.</summary>
    public int MaxAbandoned { get; init; } = 4;
    public TimeSpan RunTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>--no-pdb: do not read debug symbols.</summary>
    public bool NoPdb { get; init; }
    /// <summary>--pdb-path: directories searched for .pdb files after the assembly's own directory.</summary>
    public IReadOnlyList<string> PdbPaths { get; init; } = Array.Empty<string>();
    /// <summary>--source: show source lines next to IL (needs a PDB).</summary>
    public bool ShowSource { get; init; }
    /// <summary>--source-path: directories searched for source files not found at the PDB path.</summary>
    public IReadOnlyList<string> SourcePaths { get; init; } = Array.Empty<string>();
    /// <summary>--uses, --find-string, --field-refs (empty: no cross-reference filter).</summary>
    public CrossRefs.CrossReferenceQuery Query { get; init; } = CrossRefs.CrossReferenceQuery.None;
    /// <summary>--audit: rules to run (null: no audit). Methods without findings are not reported.</summary>
    public IReadOnlyList<Audit.IRule>? AuditRules { get; init; }
    /// <summary>--min-level: findings below it are dropped.</summary>
    public Audit.Severity MinLevel { get; init; } = Audit.Severity.Note;
    /// <summary>--fail-on: exit code 4 when a finding of this level or higher remains.</summary>
    public Audit.Severity? FailOn { get; init; }
    /// <summary>--detect-obfuscation: verdict and signs per assembly.</summary>
    public bool DetectObfuscation { get; init; }
    /// <summary>Restrictions for processes that run analyzed code (--allow-run).</summary>
    public SandboxOptions Sandbox { get; init; } = new();
}
