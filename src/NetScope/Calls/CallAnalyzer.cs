using System.Reflection.Emit;
using System.Reflection.Metadata;
using NetScope.Catalog;
using NetScope.IL;
using NetScope.Metadata;

namespace NetScope.Calls;

/// <summary>One call or method reference inside a method body.</summary>
public sealed class CallSite
{
    public int Offset { get; init; }
    /// <summary>call, callvirt, newobj, calli, ldftn, ldvirtftn.</summary>
    public required string Kind { get; init; }
    /// <summary>Null for calli (target is unknown statically).</summary>
    public MethodRef? Target { get; init; }
    public string? CalliSignature { get; init; }

    public string Display => Target?.Display ?? "(indirect) " + CalliSignature;
    public bool IsInternal => Target?.IsInternal ?? false;
    public string Scope => Target == null ? "indirect" : Target.IsInternal ? "internal" : "external";
}

public interface ICallAnalyzer
{
    IReadOnlyList<CallSite> Analyze(MethodIL il, MemberResolver resolver);
}

public sealed class CallAnalyzer : ICallAnalyzer
{
    public IReadOnlyList<CallSite> Analyze(MethodIL il, MemberResolver resolver)
    {
        var calls = new List<CallSite>();
        foreach (var ins in il.Instructions)
        {
            var v = ins.OpCode.Value;
            if (v == OpCodes.Call.Value || v == OpCodes.Callvirt.Value || v == OpCodes.Newobj.Value ||
                v == OpCodes.Ldftn.Value || v == OpCodes.Ldvirtftn.Value || v == OpCodes.Jmp.Value)
            {
                calls.Add(new CallSite { Offset = ins.Offset, Kind = ins.Name, Target = resolver.ResolveMethod(ins.Handle) });
            }
            else if (v == OpCodes.Calli.Value)
            {
                string sig;
                try
                {
                    sig = resolver.ResolveStandaloneSignature((StandaloneSignatureHandle)ins.Handle, out _);
                }
                catch (Exception)
                {
                    sig = $"0x{ins.Token:X8}";
                }
                calls.Add(new CallSite { Offset = ins.Offset, Kind = ins.Name, CalliSignature = sig });
            }
        }
        return calls;
    }
}

public sealed record CallerInfo(MethodModel Caller, int Offset, string Kind);

/// <summary>Group-wide index: method definitions by key and the reverse "who calls me" lists.</summary>
public sealed class CallIndex
{
    private readonly Dictionary<string, MethodModel> _definitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<CallerInfo>> _callers = new(StringComparer.Ordinal);
    private readonly List<(MethodModel Caller, CallSite Site)> _edges = new();

    public void AddDefinitions(IEnumerable<MethodModel> methods)
    {
        foreach (var m in methods) _definitions.TryAdd(m.Key, m);
    }

    public void AddCalls(MethodModel caller, IReadOnlyList<CallSite> calls)
    {
        foreach (var c in calls)
        {
            if (c.Target == null) continue;
            if (!_callers.TryGetValue(c.Target.Key, out var list)) _callers[c.Target.Key] = list = new List<CallerInfo>();
            list.Add(new CallerInfo(caller, c.Offset, c.Kind));
            _edges.Add((caller, c));
        }
    }

    public IReadOnlyList<CallerInfo> CallersOf(MethodModel method) =>
        _callers.TryGetValue(method.Key, out var list) ? list : Array.Empty<CallerInfo>();

    /// <summary>Finds the definition of a call target among the input files, if any.</summary>
    public MethodModel? Resolve(MethodRef target) => _definitions.TryGetValue(target.Key, out var m) ? m : null;

    /// <summary>Edges between methods of the group (both ends are definitions in input files).</summary>
    public IEnumerable<CallGraphEdge> GroupEdges(Func<MethodModel, bool> include)
    {
        var grouped = new Dictionary<(MethodModel, MethodModel), CallGraphEdge>();
        var order = new List<(MethodModel, MethodModel)>();
        foreach (var (caller, site) in _edges)
        {
            var callee = Resolve(site.Target!);
            if (callee == null || !(include(caller) || include(callee))) continue;
            var key = (caller, callee);
            if (!grouped.TryGetValue(key, out var edge))
            {
                grouped[key] = edge = new CallGraphEdge(caller, callee);
                order.Add(key);
            }
            edge.Sites.Add(site);
        }
        return order.Select(k => grouped[k]);
    }
}

public sealed class CallGraphEdge
{
    public CallGraphEdge(MethodModel caller, MethodModel callee)
    {
        Caller = caller;
        Callee = callee;
    }

    public MethodModel Caller { get; }
    public MethodModel Callee { get; }
    public List<CallSite> Sites { get; } = new();
}
