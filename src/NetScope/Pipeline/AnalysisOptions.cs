using NetScope.Input;
using NetScope.Reporting;

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
}
