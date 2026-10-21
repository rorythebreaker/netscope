using System.Reflection.Emit;
using System.Reflection.Metadata;
using NetScope.Catalog;
using NetScope.CrossRefs;
using NetScope.IL;
using NetScope.Metadata;
using NetScope.Symbols;

namespace NetScope.Audit;

/// <summary>Finding levels, in increasing order of severity (SARIF names).</summary>
public enum Severity { Note, Warning, Error }

/// <summary>One problem found by a rule in a method.</summary>
public sealed record Finding(string RuleId, Severity Level, string Message, MethodModel Method, int? Offset, SourcePoint? Source);

/// <summary>
/// A security rule. Rules only read IL and metadata; nothing is executed. Every non-abstract class that
/// implements this interface is a rule (<see cref="RuleSet"/> finds them), so a new rule is one new file.
/// </summary>
public interface IRule
{
    /// <summary>NS001, NS002, ...</summary>
    string Id { get; }
    /// <summary>Short PascalCase name (SARIF rule name).</summary>
    string Name { get; }
    string Title { get; }
    /// <summary>What the rule looks for and why it matters.</summary>
    string Description { get; }
    Severity Level { get; }

    void Check(RuleContext context);
}

/// <summary>What a rule sees of one method, with helpers for the usual IL patterns.</summary>
public sealed class RuleContext
{
    private readonly Func<MethodDefinitionHandle, MethodIL?> _loadDefinition;
    private readonly List<Finding> _findings;

    internal RuleContext(MethodModel method, MethodIL? il, MemberResolver resolver, Func<MethodDefinitionHandle, MethodIL?> loadDefinition, List<Finding> findings)
    {
        Method = method;
        IL = il;
        Resolver = resolver;
        _loadDefinition = loadDefinition;
        _findings = findings;
    }

    public MethodModel Method { get; }
    /// <summary>Null for methods without an IL body (P/Invoke declarations, abstract methods).</summary>
    public MethodIL? IL { get; }
    public MemberResolver Resolver { get; }
    public IReadOnlyList<ILInstruction> Instructions => IL?.Instructions ?? (IReadOnlyList<ILInstruction>)Array.Empty<ILInstruction>();

    public void Report(IRule rule, ILInstruction? at, string message)
    {
        var source = at != null ? Method.Assembly.Symbols?.At(Method.Handle, at.Offset) : null;
        _findings.Add(new Finding(rule.Id, rule.Level, message, Method, at?.Offset, source));
    }

    /// <summary>call, callvirt, newobj, ldftn, ldvirtftn with the resolved target and its instruction index.</summary>
    public IEnumerable<(int Index, ILInstruction Instruction, MethodRef Target)> Calls()
    {
        var list = Instructions;
        for (int i = 0; i < list.Count; i++)
            if (list[i].OpCode.OperandType == OperandType.InlineMethod)
                yield return (i, list[i], Resolver.ResolveMethod(list[i].Handle));
    }

    /// <summary>String literals (ldstr) with their instruction index.</summary>
    public IEnumerable<(int Index, ILInstruction Instruction, string Value)> Strings()
    {
        var list = Instructions;
        for (int i = 0; i < list.Count; i++)
            if (list[i].OpCode.Value == OpCodes.Ldstr.Value && CrossReferenceQuery.Literal(list[i], Resolver) is { } value)
                yield return (i, list[i], value);
    }

    /// <summary>"System.Diagnostics.Process" for a target of any instantiation.</summary>
    public static string TypeOf(MethodRef target) => CrossReferenceQuery.TypeForMatching(target.DeclaringType);

    public static bool Is(MethodRef target, string type, string? name = null) =>
        TypeOf(target) == type && (name == null || NameOf(target) == name);

    /// <summary>Method name without generic arguments: "GetDelegateForFunctionPointer&lt;Action&gt;" -> "GetDelegateForFunctionPointer".</summary>
    public static string NameOf(MethodRef target)
    {
        int lt = target.Name.IndexOf('<', StringComparison.Ordinal);
        return lt > 0 ? target.Name.Substring(0, lt) : target.Name;
    }

    /// <summary>The body of a method of this assembly (e.g. a lambda passed as a callback), if it has one.</summary>
    public MethodIL? LoadDefinition(MethodRef target) => target.Definition.IsNil ? null : _loadDefinition(target.Definition);

    /// <summary>
    /// The instruction that pushed a value consumed by the instruction at <paramref name="index"/>:
    /// <paramref name="fromTop"/> = 0 is the last pushed value (the last argument of a call).
    /// Walks back through the same straight-line code; returns null at a branch target or on unknown stack effects.
    /// A value loaded from a local is followed to the last store into that local.
    /// </summary>
    public ILInstruction? Producer(int index, int fromTop)
    {
        var list = Instructions;
        int need = fromTop; // number of values above the wanted one still to skip
        for (int i = index - 1; i >= 0; i--)
        {
            var ins = list[i];
            int push = Pushes(ins), pop = Pops(ins);
            if (push < 0 || pop < 0) return null;
            if (push > 0)
            {
                if (need == 0) return Follow(i);
                need -= push;
                if (need < 0) return null; // dup and similar: the value came from a multi-push
            }
            need += pop;
            // Code before a branch target may not be the path that produced the value.
            if (IsBranchTarget(ins.Offset)) return null;
        }
        return null;
    }

    /// <summary>ldloc -> the producer of the value stored by the closest preceding stloc to the same local.</summary>
    private ILInstruction? Follow(int index)
    {
        var list = Instructions;
        var ins = list[index];
        if (LocalIndex(ins, load: true) is not int local) return ins;
        for (int i = index - 1; i >= 0; i--)
            if (LocalIndex(list[i], load: false) == local) return Producer(i + 1, 0) ?? list[i];
        return ins;
    }

    private HashSet<int>? _targets;

    private bool IsBranchTarget(int offset)
    {
        if (_targets == null)
        {
            _targets = new HashSet<int>();
            foreach (var ins in Instructions)
            {
                if (ins.IsBranch) _targets.Add(ins.BranchTarget);
                else if (ins.IsSwitch) _targets.UnionWith(ins.SwitchTargets);
            }
            if (IL != null)
                foreach (var r in IL.Body.ExceptionRegions)
                {
                    _targets.Add(r.HandlerOffset);
                    if (r.Kind == ExceptionRegionKind.Filter) _targets.Add(r.FilterOffset);
                }
        }
        return _targets.Contains(offset);
    }

    private static int? LocalIndex(ILInstruction ins, bool load)
    {
        var v = ins.OpCode.Value;
        if (load)
        {
            if (v == OpCodes.Ldloc_0.Value) return 0;
            if (v == OpCodes.Ldloc_1.Value) return 1;
            if (v == OpCodes.Ldloc_2.Value) return 2;
            if (v == OpCodes.Ldloc_3.Value) return 3;
            if (v == OpCodes.Ldloc_S.Value || v == OpCodes.Ldloc.Value) return (int)ins.Operand!;
        }
        else
        {
            if (v == OpCodes.Stloc_0.Value) return 0;
            if (v == OpCodes.Stloc_1.Value) return 1;
            if (v == OpCodes.Stloc_2.Value) return 2;
            if (v == OpCodes.Stloc_3.Value) return 3;
            if (v == OpCodes.Stloc_S.Value || v == OpCodes.Stloc.Value) return (int)ins.Operand!;
        }
        return null;
    }

    private int Pushes(ILInstruction ins)
    {
        var op = ins.OpCode;
        if (op.OperandType == OperandType.InlineMethod)
        {
            var target = Resolver.ResolveMethod(ins.Handle);
            if (op.Value == OpCodes.Newobj.Value || op.Value == OpCodes.Ldftn.Value || op.Value == OpCodes.Ldvirtftn.Value) return 1;
            return target.ReturnsVoid ? 0 : 1;
        }
        return op.StackBehaviourPush switch
        {
            StackBehaviour.Push0 => 0,
            StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or StackBehaviour.Pushr4 or StackBehaviour.Pushr8 or StackBehaviour.Pushref => 1,
            StackBehaviour.Push1_push1 => 2,
            _ => -1, // varpush outside calls (calli)
        };
    }

    private int Pops(ILInstruction ins)
    {
        var op = ins.OpCode;
        if (op.OperandType == OperandType.InlineMethod)
        {
            var target = Resolver.ResolveMethod(ins.Handle);
            if (op.Value == OpCodes.Ldftn.Value) return 0;
            if (op.Value == OpCodes.Ldvirtftn.Value) return 1;
            if (op.Value == OpCodes.Newobj.Value) return target.ParameterCount;
            return target.ParameterCount + (target.HasThis ? 1 : 0);
        }
        return op.StackBehaviourPop switch
        {
            StackBehaviour.Pop0 => 0,
            StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
            StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8 or StackBehaviour.Popi_popr4
                or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
            StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8 or StackBehaviour.Popref_popi_popr4
                or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref or StackBehaviour.Popref_popi_pop1 => 3,
            _ => -1, // varpop: ret, calli
        };
    }

    /// <summary>Integer constant pushed by ldc.i4*, or null.</summary>
    public static int? IntConstant(ILInstruction? ins)
    {
        if (ins == null) return null;
        var v = ins.OpCode.Value;
        if (v >= OpCodes.Ldc_I4_M1.Value && v <= OpCodes.Ldc_I4_8.Value) return v - OpCodes.Ldc_I4_0.Value;
        if (v == OpCodes.Ldc_I4_S.Value || v == OpCodes.Ldc_I4.Value) return (int)ins.Operand!;
        return null;
    }
}
