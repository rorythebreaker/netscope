using NetScope.Asm;
using NetScope.Calls;
using NetScope.Catalog;
using NetScope.Cfg;
using NetScope.Diagnostics;
using NetScope.IL;
using NetScope.Input;
using NetScope.Loading;
using NetScope.Metadata;
using NetScope.Pseudo;
using NetScope.Reporting;

namespace NetScope.Pipeline;

/// <summary>
/// Runs the pipeline: InputResolver -> AssemblyLoader -> MethodCatalog -> CallAnalyzer -> CfgBuilder
/// -> PseudoGenerator -> AsmProvider -> Reporter. Modules 3-7 run only for the requested groups.
/// </summary>
public sealed class Analyzer
{
    public const int ExitOk = 0, ExitPartial = 1, ExitBadArguments = 2, ExitNothingOpened = 3;

    private readonly IInputResolver _inputs;
    private readonly IAssemblyLoader _loader;
    private readonly IMethodCatalog _catalog;
    private readonly ICallAnalyzer _calls;
    private readonly ICfgBuilder _cfg;

    public Analyzer(IInputResolver? inputs = null, IAssemblyLoader? loader = null, IMethodCatalog? catalog = null, ICallAnalyzer? calls = null, ICfgBuilder? cfg = null)
    {
        _inputs = inputs ?? new InputResolver();
        _loader = loader ?? new AssemblyLoader();
        _catalog = catalog ?? new MethodCatalog();
        _calls = calls ?? new CallAnalyzer();
        _cfg = cfg ?? new CfgBuilder();
    }

    private sealed class Unit
    {
        public Unit(LoadedAssembly asm) => Asm = asm;
        public LoadedAssembly Asm { get; }
        public MemberResolver Members { get; set; } = null!;
        public IReadOnlyList<MethodModel> Methods { get; set; } = Array.Empty<MethodModel>();
        public Dictionary<MethodModel, IReadOnlyList<CallSite>> Calls { get; } = new();
        public string? Error { get; set; }
    }

    public int Run(AnalysisOptions o, TextWriter stdout)
    {
        var groups = o.Groups;
        var filter = new MethodFilter(o.Filters, o.Types);
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, o.Threads) };

        var abandoned = new AbandonedDecompilations(o.MaxAbandoned);
        var paths = _inputs.Resolve(o.Inputs, o.Recursive);
        if (paths.Count == 0)
        {
            Log.Error(null, "no input files found");
            return ExitNothingOpened;
        }
        Log.Info($"{paths.Count} input file(s)");

        // AssemblyLoader: check PE + CLR header, map files read-only.
        var loaded = new LoadedAssembly?[paths.Count];
        Parallel.For(0, paths.Count, parallel, i =>
        {
            var asm = _loader.TryLoad(i, paths[i], out var reason);
            if (asm == null) Log.Skipped(paths[i], "skipped: " + reason);
            loaded[i] = asm;
        });
        var units = loaded.Where(a => a != null).Select(a => new Unit(a!)).ToArray();
        if (units.Length == 0)
        {
            Log.Error(null, "none of the input files could be opened as a managed assembly");
            return ExitNothingOpened;
        }

        bool perMethod = groups.Methods || groups.Calls || groups.Cfg || groups.Pseudo || groups.IL || groups.Native;
        bool needCalls = groups.Calls || groups.CallGraph;
        using var references = new ReferenceResolver(units.Select(u => u.Asm), o.RefPaths);
        try
        {
            // MethodCatalog (+ CallAnalyzer over all methods, so that reverse references cover the whole group).
            Parallel.ForEach(units, parallel, u =>
            {
                references.CheckReferences(u.Asm);
                u.Members = new MemberResolver(u.Asm.Metadata, u.Asm.Name);
                try
                {
                    u.Methods = _catalog.Build(u.Asm, u.Members, filter);
                }
                catch (Exception ex)
                {
                    u.Error = "cannot read metadata: " + ex.Message;
                    Log.Error(u.Asm.FileName, u.Error);
                    return;
                }
                if (!needCalls) return;
                foreach (var m in u.Methods)
                {
                    if (!m.HasBody) continue;
                    try
                    {
                        var il = MethodIL.Load(m, u.Members, out _, out _);
                        if (il != null) u.Calls[m] = _calls.Analyze(il, u.Members);
                    }
                    catch (Exception)
                    {
                        // Reported in the per-method stage for selected methods.
                    }
                }
            });

            CallIndex? index = null;
            if (needCalls)
            {
                index = new CallIndex();
                foreach (var u in units) index.AddDefinitions(u.Methods);
                foreach (var u in units)
                foreach (var m in u.Methods)
                    if (u.Calls.TryGetValue(m, out var calls)) index.AddCalls(m, calls);
            }

            var sink = OutputSink.Create(o, stdout, () => CreateReporter(o));
            sink.Begin();
            if (perMethod)
            {
                // Files are analyzed in parallel; output keeps the input order.
                var rendered = units
                    .Select((u, i) => (Unit: u, Index: i))
                    .AsParallel().AsOrdered().WithDegreeOfParallelism(parallel.MaxDegreeOfParallelism)
                    .Select(x => (x.Unit, Text: RenderUnit(x.Unit, o, index, references, abandoned, sink.FirstFlag(x.Index), sink.Reporter)));
                foreach (var (u, text) in rendered)
                {
                    sink.WriteAssembly(u.Asm, text);
                    foreach (var m in u.Methods) m.ReleaseBody();
                }
            }
            if (groups.CallGraph && index != null)
            {
                var edges = index.GroupEdges(m => m.Selected).ToList();
                sink.WriteCallGraph(edges, perMethod);
            }
            sink.Finish();
        }
        finally
        {
            foreach (var u in units) u.Asm.Dispose();
        }
        return Log.Failures > 0 ? ExitPartial : ExitOk;
    }

    internal static IReporter CreateReporter(AnalysisOptions o)
    {
        var g = o.Groups;
        bool cfgGraphs = g.Cfg || !g.CallGraph;
        return o.Format switch
        {
            "json" => new JsonReporter(g),
            "dot" => new DotReporter(cfgGraphs),
            "mermaid" => new MermaidReporter(cfgGraphs),
            _ => new TextReporter(g, o.Color),
        };
    }

    private string RenderUnit(Unit u, AnalysisOptions o, CallIndex? index, ReferenceResolver references, AbandonedDecompilations abandoned, bool first, IReporter reporter)
    {
        var groups = o.Groups;
        var report = new AssemblyReport { Assembly = u.Asm };
        if (u.Error != null) report.Errors.Add(u.Error);
        var file = u.Asm.FileName;
        var pseudo = groups.Pseudo ? new PseudoGenerator(u.Asm, references.For(u.Asm), u.Members, o.Raw, o.DecompileTimeout, abandoned) : null;
        var asmProvider = new AsmProvider(u.Members, o.AllowRun, o.RefPaths, o.RunTimeout);
        bool graphs = o.Format is "dot" or "mermaid";
        bool needCfg = groups.Cfg || (graphs && !groups.CallGraph);

        var selected = u.Methods.Where(m => m.Selected).ToList();
        foreach (var m in selected)
        {
            var r = new MethodReport { Method = m };
            report.Methods.Add(r);
            try
            {
                MethodIL? il = null;
                var body = m.GetBody(out var bodyError);
                if (body != null) r.CodeSize = body.GetILReader().Length;
                else if (m.HasBody)
                {
                    r.Errors.Add(bodyError!);
                    Log.Error(Log.At(file, m.FullName), bodyError!);
                }

                if (body != null && (needCfg || groups.IL || groups.Pseudo))
                {
                    il = MethodIL.Load(m, u.Members, out var ilError, out var errorOffset);
                    if (il == null)
                    {
                        r.Errors.Add(errorOffset is int off ? $"IL_{off:X4}: {ilError}" : ilError!);
                        Log.Error(Log.At(file, m.FullName, errorOffset), ilError!);
                    }
                }

                if (groups.Calls && index != null)
                {
                    r.Calls = u.Calls.TryGetValue(m, out var calls)
                        ? calls.Select(c => new CallReport(c, c.Target == null ? null : index.Resolve(c.Target))).ToList()
                        : new List<CallReport>();
                    r.Callers = index.CallersOf(m);
                }

                if (needCfg && il != null)
                {
                    try
                    {
                        r.Cfg = _cfg.Build(il, u.Members);
                        foreach (var w in r.Cfg.Warnings) Log.Warning(Log.At(file, m.FullName), w);
                    }
                    catch (Exception ex)
                    {
                        r.Errors.Add("cannot build CFG: " + ex.Message);
                        Log.Error(Log.At(file, m.FullName), "cannot build CFG: " + ex.Message);
                    }
                }

                if (pseudo != null)
                {
                    r.Pseudo = pseudo.Generate(m, il, r.Cfg);
                    if (r.Pseudo.Reason != null && m.HasBody)
                        Log.Warning(Log.At(file, m.FullName), "pseudocode fallback: " + r.Pseudo.Reason);
                }

                if (il != null && (groups.IL || r.Cfg != null))
                {
                    var listing = asmProvider.GetIL(il);
                    if (groups.IL) r.IL = listing;
                    if (r.Cfg != null) r.Operands = listing.Lines.ToDictionary(l => l.Offset, l => l.Operand);
                }
            }
            catch (Exception ex)
            {
                r.Errors.Add("analysis failed: " + ex.Message);
                Log.Error(Log.At(file, m.FullName), "analysis failed: " + ex.Message);
            }
        }

        if (groups.Native && selected.Count > 0)
        {
            try
            {
                var natives = asmProvider.GetNative(u.Asm, selected);
                foreach (var r in report.Methods)
                    if (natives.TryGetValue(r.Method, out var n)) r.Native = n;
            }
            catch (Exception ex)
            {
                report.Errors.Add("native code: " + ex.Message);
                Log.Error(file, "native code: " + ex.Message);
            }
        }

        Log.Info($"{file}: {selected.Count} of {u.Methods.Count} method(s) reported");
        return reporter.RenderAssembly(report, first);
    }
}
