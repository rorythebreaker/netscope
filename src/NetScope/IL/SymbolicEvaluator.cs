using System.Globalization;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using NetScope.Metadata;

namespace NetScope.IL;

public enum Hint { Unknown, Bool, Number, Reference }

/// <summary>A symbolic value on the evaluation stack: C#-like text with precedence.</summary>
public sealed class Expr
{
    public const int Atom = 110, Primary = 100, Unary = 90, Mult = 80, Add = 70, Shift = 60, Relational = 50, Equality = 45, BitAnd = 40, Xor = 39, BitOr = 38, Assign = 0;

    public Expr(string text, int prec, Hint hint = Hint.Unknown, string? negated = null)
    {
        Text = text;
        Prec = prec;
        Hint = hint;
        Negated = negated;
    }

    public string Text { get; }
    public int Prec { get; }
    public Hint Hint { get; }
    /// <summary>Text of the logical negation, when the value is a condition.</summary>
    public string? Negated { get; init; }
    /// <summary>For "x as T": the text of "x is T", used when the value is tested for null.</summary>
    public string? IsTest { get; init; }
    /// <summary>The value is the address of a variable: "ref x".</summary>
    public string? AddressOf { get; init; }

    public bool IsAtom => Prec >= Atom;

    /// <summary>Text wrapped in parentheses when its precedence is lower than required.</summary>
    public string P(int min) => Prec < min ? "(" + Text + ")" : Text;

    public static Expr Name(string name, Hint hint = Hint.Unknown) => new(name, Atom, hint);

    public override string ToString() => Text;

    public static Hint HintOf(string? type) => type switch
    {
        null => Hint.Unknown,
        "bool" => Hint.Bool,
        "int" or "uint" or "long" or "ulong" or "short" or "ushort" or "byte" or "sbyte" or "char" or "float" or "double" or "nint" or "nuint" => Hint.Number,
        "void" => Hint.Unknown,
        _ when type.StartsWith("ref ", StringComparison.Ordinal) || type.EndsWith('*') => Hint.Unknown,
        _ => Hint.Reference,
    };
}

/// <summary>
/// Simulates the IL evaluation stack of a basic block.
/// In inline mode it rebuilds expressions (used for branch labels); in three-address mode every
/// intermediate value is assigned to a temporary tN, so side effects keep their IL order.
/// </summary>
public sealed class SymbolicEvaluator
{
    private readonly MethodIL _il;
    private readonly MemberResolver _r;
    private readonly bool _tac;
    private int _temp;

    public SymbolicEvaluator(MethodIL il, MemberResolver resolver, bool threeAddress)
    {
        _il = il;
        _r = resolver;
        _tac = threeAddress;
    }

    public sealed class Result
    {
        public List<string> Statements { get; } = new();
        public List<Expr> ExitStack { get; set; } = new();
        /// <summary>For conditional branches: the condition under which the branch is taken.</summary>
        public Expr? Condition { get; set; }
        public Expr? SwitchValue { get; set; }
        public ILInstruction? Terminator { get; set; }
    }

    public Result Run(IReadOnlyList<ILInstruction> instructions, IReadOnlyList<Expr> entryStack)
    {
        var res = new Result();
        var stack = new List<Expr>(entryStack);
        for (int i = 0; i < instructions.Count; i++)
        {
            var ins = instructions[i];
            var next = i + 1 < instructions.Count ? instructions[i + 1] : null;
            bool skipNext = Step(ins, next, stack, res);
            if (ins.EndsBlock) res.Terminator = ins;
            if (skipNext) i++;
        }
        res.ExitStack = stack;
        return res;
    }

    // ---------------------------------------------------------------- stack helpers

    private static Expr Pop(List<Expr> stack)
    {
        if (stack.Count == 0) return Expr.Name("s?");
        var e = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        return e;
    }

    private static List<Expr> PopN(List<Expr> stack, int n)
    {
        var list = new Expr[n];
        for (int i = n - 1; i >= 0; i--) list[i] = Pop(stack);
        return list.ToList();
    }

    private void Push(List<Expr> stack, Expr e, Result res)
    {
        if (_tac && !e.IsAtom) e = Temp(e, res);
        stack.Add(e);
    }

    private Expr Temp(Expr e, Result res)
    {
        var name = "t" + _temp++;
        res.Statements.Add($"{name} = {e.Text};");
        return new Expr(name, Expr.Atom, e.Hint, e.Negated != null ? "!" + name : null);
    }

    private void Store(string variable, Expr value, List<Expr> stack, Result res)
    {
        // In three-address mode a stacked read of the same variable must keep the old value.
        if (_tac)
        {
            for (int i = 0; i < stack.Count; i++)
                if (stack[i].Text == variable) stack[i] = Temp(stack[i], res);
        }
        res.Statements.Add($"{variable} = {value.Text};");
    }

    private Expr Arg(int index)
    {
        var type = index < _il.ArgTypes.Length ? _il.ArgTypes[index] : null;
        return Expr.Name(_il.ArgName(index), Expr.HintOf(type));
    }

    private Expr Local(int index)
    {
        var type = index < _il.LocalTypes.Length ? _il.LocalTypes[index] : null;
        return Expr.Name(_il.LocalName(index), Expr.HintOf(type));
    }

    private static Expr Literal(string text, Hint hint) => new(text, Expr.Atom, hint);

    private static Expr AddressOf(string variable) => new("ref " + variable, Expr.Atom) { AddressOf = variable };

    /// <summary>Text of *p, or the variable itself when p is "ref x".</summary>
    private static string Deref(Expr pointer) => pointer.AddressOf ?? "*" + pointer.P(Expr.Unary);

    private static string Receiver(Expr obj) => obj.AddressOf ?? obj.P(Expr.Primary);

    private string TypeOperand(ILInstruction ins) => _r.ResolveType(ins.Handle, _il.Context).Display;

    // ---------------------------------------------------------------- conditions

    public static Expr Truthy(Expr v)
    {
        if (v.Negated != null) return v;
        if (v.IsTest != null) return new Expr(v.IsTest, Expr.Relational, Hint.Bool, "!(" + v.IsTest + ")");
        return v.Hint switch
        {
            Hint.Number => new Expr(v.P(Expr.Equality) + " != 0", Expr.Equality, Hint.Bool, v.P(Expr.Equality) + " == 0"),
            Hint.Reference => new Expr(v.P(Expr.Equality) + " != null", Expr.Equality, Hint.Bool, v.P(Expr.Equality) + " == null"),
            _ => new Expr(v.Text, v.Prec, Hint.Bool, "!" + v.P(Expr.Unary)),
        };
    }

    public static Expr Negate(Expr cond)
    {
        var t = Truthy(cond);
        return new Expr(t.Negated!, Expr.Equality, Hint.Bool, t.Text);
    }

    private static readonly Dictionary<string, string> Opposite = new()
    {
        ["=="] = "!=", ["!="] = "==", ["<"] = ">=", [">="] = "<", [">"] = "<=", ["<="] = ">",
    };

    public static Expr Compare(Expr a, Expr b, string op)
    {
        // x == 0 / x != 0 on booleans reads better as !x / x.
        if ((b.Text is "0" or "false") && a.Hint == Hint.Bool && op is "==" or "!=")
        {
            var t = Truthy(a);
            return op == "==" ? new Expr(t.Negated!, Expr.Unary, Hint.Bool, t.Text) : t;
        }
        int prec = op is "==" or "!=" ? Expr.Equality : Expr.Relational;
        var text = $"{a.P(prec)} {op} {b.P(prec + 1)}";
        var neg = $"{a.P(prec)} {Opposite[op]} {b.P(prec + 1)}";
        return new Expr(text, prec, Hint.Bool, neg);
    }

    // ---------------------------------------------------------------- instructions

    private bool Step(ILInstruction ins, ILInstruction? next, List<Expr> stack, Result res)
    {
        var code = (ILOpCode)(ushort)ins.OpCode.Value;
        switch (code)
        {
            case ILOpCode.Nop:
            case ILOpCode.Constrained:
            case ILOpCode.Volatile:
            case ILOpCode.Unaligned:
            case ILOpCode.Tail:
            case ILOpCode.Readonly:
                return false;
            case ILOpCode.Break:
                res.Statements.Add("Debugger.Break();");
                return false;

            case ILOpCode.Ldarg_0: Push(stack, Arg(0), res); return false;
            case ILOpCode.Ldarg_1: Push(stack, Arg(1), res); return false;
            case ILOpCode.Ldarg_2: Push(stack, Arg(2), res); return false;
            case ILOpCode.Ldarg_3: Push(stack, Arg(3), res); return false;
            case ILOpCode.Ldarg_s:
            case ILOpCode.Ldarg: Push(stack, Arg((int)ins.Operand!), res); return false;
            case ILOpCode.Ldarga_s:
            case ILOpCode.Ldarga: Push(stack, AddressOf(_il.ArgName((int)ins.Operand!)), res); return false;
            case ILOpCode.Starg_s:
            case ILOpCode.Starg: Store(_il.ArgName((int)ins.Operand!), Pop(stack), stack, res); return false;

            case ILOpCode.Ldloc_0: Push(stack, Local(0), res); return false;
            case ILOpCode.Ldloc_1: Push(stack, Local(1), res); return false;
            case ILOpCode.Ldloc_2: Push(stack, Local(2), res); return false;
            case ILOpCode.Ldloc_3: Push(stack, Local(3), res); return false;
            case ILOpCode.Ldloc_s:
            case ILOpCode.Ldloc: Push(stack, Local((int)ins.Operand!), res); return false;
            case ILOpCode.Ldloca_s:
            case ILOpCode.Ldloca: Push(stack, AddressOf(_il.LocalName((int)ins.Operand!)), res); return false;
            case ILOpCode.Stloc_0: Store(_il.LocalName(0), Pop(stack), stack, res); return false;
            case ILOpCode.Stloc_1: Store(_il.LocalName(1), Pop(stack), stack, res); return false;
            case ILOpCode.Stloc_2: Store(_il.LocalName(2), Pop(stack), stack, res); return false;
            case ILOpCode.Stloc_3: Store(_il.LocalName(3), Pop(stack), stack, res); return false;
            case ILOpCode.Stloc_s:
            case ILOpCode.Stloc: Store(_il.LocalName((int)ins.Operand!), Pop(stack), stack, res); return false;

            case ILOpCode.Ldnull: Push(stack, Literal("null", Hint.Reference), res); return false;
            case ILOpCode.Ldc_i4_m1: Push(stack, Literal("-1", Hint.Number), res); return false;
            case ILOpCode.Ldc_i4_0:
            case ILOpCode.Ldc_i4_1:
            case ILOpCode.Ldc_i4_2:
            case ILOpCode.Ldc_i4_3:
            case ILOpCode.Ldc_i4_4:
            case ILOpCode.Ldc_i4_5:
            case ILOpCode.Ldc_i4_6:
            case ILOpCode.Ldc_i4_7:
            case ILOpCode.Ldc_i4_8:
                Push(stack, Literal((code - ILOpCode.Ldc_i4_0).ToString(CultureInfo.InvariantCulture), Hint.Number), res);
                return false;
            case ILOpCode.Ldc_i4_s:
            case ILOpCode.Ldc_i4: Push(stack, Literal(((int)ins.Operand!).ToString(CultureInfo.InvariantCulture), Hint.Number), res); return false;
            case ILOpCode.Ldc_i8: Push(stack, Literal(((long)ins.Operand!).ToString(CultureInfo.InvariantCulture) + "L", Hint.Number), res); return false;
            case ILOpCode.Ldc_r4: Push(stack, Literal(((float)ins.Operand!).ToString("R", CultureInfo.InvariantCulture) + "f", Hint.Number), res); return false;
            case ILOpCode.Ldc_r8: Push(stack, Literal(((double)ins.Operand!).ToString("R", CultureInfo.InvariantCulture), Hint.Number), res); return false;
            case ILOpCode.Ldstr:
            {
                string s;
                try { s = Names.EscapeString(_r.Metadata.GetUserString((UserStringHandle)System.Reflection.Metadata.Ecma335.MetadataTokens.Handle(ins.Token))); }
                catch (Exception) { s = "\"?\""; }
                Push(stack, Literal(s, Hint.Reference), res);
                return false;
            }

            case ILOpCode.Dup:
            {
                var v = Pop(stack);
                if (_tac && !v.IsAtom) v = Temp(v, res);
                stack.Add(v);
                stack.Add(v);
                return false;
            }
            case ILOpCode.Pop:
            {
                var v = Pop(stack);
                if (!v.IsAtom) res.Statements.Add(v.Text + ";");
                return false;
            }

            case ILOpCode.Jmp:
                res.Statements.Add("jmp " + _r.ResolveMethod(ins.Handle).Display + ";");
                return false;
            case ILOpCode.Call:
            case ILOpCode.Callvirt:
                return Call(ins, next, stack, res, isNew: false);
            case ILOpCode.Newobj:
                return Call(ins, next, stack, res, isNew: true);
            case ILOpCode.Calli:
                return Calli(ins, next, stack, res);
            case ILOpCode.Ret:
                res.Statements.Add(_il.ReturnType == "void" ? "return;" : "return " + Pop(stack).Text + ";");
                return false;

            case ILOpCode.Br:
            case ILOpCode.Br_s:
            case ILOpCode.Leave:
            case ILOpCode.Leave_s:
                if (code is ILOpCode.Leave or ILOpCode.Leave_s) stack.Clear();
                return false;
            case ILOpCode.Brfalse:
            case ILOpCode.Brfalse_s:
                res.Condition = Negate(Pop(stack));
                return false;
            case ILOpCode.Brtrue:
            case ILOpCode.Brtrue_s:
                res.Condition = Truthy(Pop(stack));
                return false;
            case ILOpCode.Beq: case ILOpCode.Beq_s: return Branch(stack, res, "==");
            case ILOpCode.Bne_un: case ILOpCode.Bne_un_s: return Branch(stack, res, "!=");
            case ILOpCode.Bge: case ILOpCode.Bge_s: case ILOpCode.Bge_un: case ILOpCode.Bge_un_s: return Branch(stack, res, ">=");
            case ILOpCode.Bgt: case ILOpCode.Bgt_s: case ILOpCode.Bgt_un: case ILOpCode.Bgt_un_s: return Branch(stack, res, ">");
            case ILOpCode.Ble: case ILOpCode.Ble_s: case ILOpCode.Ble_un: case ILOpCode.Ble_un_s: return Branch(stack, res, "<=");
            case ILOpCode.Blt: case ILOpCode.Blt_s: case ILOpCode.Blt_un: case ILOpCode.Blt_un_s: return Branch(stack, res, "<");
            case ILOpCode.Switch:
                res.SwitchValue = Pop(stack);
                return false;

            case ILOpCode.Ceq: return CompareOp(stack, res, "==");
            case ILOpCode.Cgt: return CompareOp(stack, res, ">");
            case ILOpCode.Cgt_un:
            {
                var b = Pop(stack);
                var a = Pop(stack);
                // ldnull; cgt.un is the compiler pattern for "x != null".
                Push(stack, Compare(a, b, b.Text == "null" ? "!=" : ">"), res);
                return false;
            }
            case ILOpCode.Clt:
            case ILOpCode.Clt_un: return CompareOp(stack, res, "<");

            case ILOpCode.Add: return Binary(stack, res, "+", Expr.Add);
            case ILOpCode.Sub: return Binary(stack, res, "-", Expr.Add);
            case ILOpCode.Mul: return Binary(stack, res, "*", Expr.Mult);
            case ILOpCode.Div:
            case ILOpCode.Div_un: return Binary(stack, res, "/", Expr.Mult);
            case ILOpCode.Rem:
            case ILOpCode.Rem_un: return Binary(stack, res, "%", Expr.Mult);
            case ILOpCode.And: return Binary(stack, res, "&", Expr.BitAnd);
            case ILOpCode.Or: return Binary(stack, res, "|", Expr.BitOr);
            case ILOpCode.Xor: return Binary(stack, res, "^", Expr.Xor);
            case ILOpCode.Shl: return Binary(stack, res, "<<", Expr.Shift);
            case ILOpCode.Shr: return Binary(stack, res, ">>", Expr.Shift);
            case ILOpCode.Shr_un: return Binary(stack, res, ">>>", Expr.Shift);
            case ILOpCode.Add_ovf:
            case ILOpCode.Add_ovf_un: return Binary(stack, res, "+", Expr.Add, @checked: true);
            case ILOpCode.Sub_ovf:
            case ILOpCode.Sub_ovf_un: return Binary(stack, res, "-", Expr.Add, @checked: true);
            case ILOpCode.Mul_ovf:
            case ILOpCode.Mul_ovf_un: return Binary(stack, res, "*", Expr.Mult, @checked: true);
            case ILOpCode.Neg:
            {
                var v = Pop(stack);
                Push(stack, new Expr("-" + v.P(Expr.Unary), Expr.Unary, Hint.Number), res);
                return false;
            }
            case ILOpCode.Not:
            {
                var v = Pop(stack);
                Push(stack, new Expr("~" + v.P(Expr.Unary), Expr.Unary, Hint.Number), res);
                return false;
            }

            case ILOpCode.Conv_i1: return Cast(stack, res, "sbyte");
            case ILOpCode.Conv_i2: return Cast(stack, res, "short");
            case ILOpCode.Conv_i4: return Cast(stack, res, "int");
            case ILOpCode.Conv_i8: return Cast(stack, res, "long");
            case ILOpCode.Conv_r4: return Cast(stack, res, "float");
            case ILOpCode.Conv_r8:
            case ILOpCode.Conv_r_un: return Cast(stack, res, "double");
            case ILOpCode.Conv_u1: return Cast(stack, res, "byte");
            case ILOpCode.Conv_u2: return Cast(stack, res, "ushort");
            case ILOpCode.Conv_u4: return Cast(stack, res, "uint");
            case ILOpCode.Conv_u8: return Cast(stack, res, "ulong");
            case ILOpCode.Conv_i: return Cast(stack, res, "nint");
            case ILOpCode.Conv_u: return Cast(stack, res, "nuint");
            case ILOpCode.Conv_ovf_i1: case ILOpCode.Conv_ovf_i1_un: return Cast(stack, res, "sbyte", true);
            case ILOpCode.Conv_ovf_i2: case ILOpCode.Conv_ovf_i2_un: return Cast(stack, res, "short", true);
            case ILOpCode.Conv_ovf_i4: case ILOpCode.Conv_ovf_i4_un: return Cast(stack, res, "int", true);
            case ILOpCode.Conv_ovf_i8: case ILOpCode.Conv_ovf_i8_un: return Cast(stack, res, "long", true);
            case ILOpCode.Conv_ovf_u1: case ILOpCode.Conv_ovf_u1_un: return Cast(stack, res, "byte", true);
            case ILOpCode.Conv_ovf_u2: case ILOpCode.Conv_ovf_u2_un: return Cast(stack, res, "ushort", true);
            case ILOpCode.Conv_ovf_u4: case ILOpCode.Conv_ovf_u4_un: return Cast(stack, res, "uint", true);
            case ILOpCode.Conv_ovf_u8: case ILOpCode.Conv_ovf_u8_un: return Cast(stack, res, "ulong", true);
            case ILOpCode.Conv_ovf_i: case ILOpCode.Conv_ovf_i_un: return Cast(stack, res, "nint", true);
            case ILOpCode.Conv_ovf_u: case ILOpCode.Conv_ovf_u_un: return Cast(stack, res, "nuint", true);

            case ILOpCode.Castclass:
            case ILOpCode.Unbox_any:
                return Cast(stack, res, TypeOperand(ins));
            case ILOpCode.Unbox:
            {
                var v = Pop(stack);
                Push(stack, new Expr("ref (" + TypeOperand(ins) + ")" + v.P(Expr.Unary), Expr.Unary), res);
                return false;
            }
            case ILOpCode.Isinst:
            {
                var v = Pop(stack);
                var t = TypeOperand(ins);
                Push(stack, new Expr(v.P(Expr.Relational) + " as " + t, Expr.Relational, Hint.Reference) { IsTest = v.P(Expr.Relational) + " is " + t }, res);
                return false;
            }
            case ILOpCode.Box:
            {
                var v = Pop(stack);
                // Boxing is transparent in C#; keep the value but mark it as a reference.
                stack.Add(new Expr(v.Text, v.Prec, Hint.Reference) { IsTest = v.IsTest, Negated = v.Negated });
                return false;
            }
            case ILOpCode.Throw:
                res.Statements.Add("throw " + Pop(stack).Text + ";");
                return false;
            case ILOpCode.Rethrow:
                res.Statements.Add("throw;");
                return false;

            case ILOpCode.Ldfld:
            {
                var f = _r.ResolveField(ins.Handle);
                var o = Pop(stack);
                Push(stack, new Expr(Receiver(o) + "." + f.Name, Expr.Primary, Expr.HintOf(f.FieldType)), res);
                return false;
            }
            case ILOpCode.Ldflda:
            {
                var f = _r.ResolveField(ins.Handle);
                var o = Pop(stack);
                var target = Receiver(o) + "." + f.Name;
                Push(stack, new Expr("ref " + target, Expr.Unary) { AddressOf = target }, res);
                return false;
            }
            case ILOpCode.Stfld:
            {
                var f = _r.ResolveField(ins.Handle);
                var v = Pop(stack);
                var o = Pop(stack);
                res.Statements.Add($"{Receiver(o)}.{f.Name} = {v.Text};");
                return false;
            }
            case ILOpCode.Ldsfld:
            {
                var f = _r.ResolveField(ins.Handle);
                Push(stack, new Expr(f.ShortDeclaringType + "." + f.Name, Expr.Primary, Expr.HintOf(f.FieldType)), res);
                return false;
            }
            case ILOpCode.Ldsflda:
            {
                var f = _r.ResolveField(ins.Handle);
                Push(stack, AddressOf(f.ShortDeclaringType + "." + f.Name), res);
                return false;
            }
            case ILOpCode.Stsfld:
            {
                var f = _r.ResolveField(ins.Handle);
                res.Statements.Add($"{f.ShortDeclaringType}.{f.Name} = {Pop(stack).Text};");
                return false;
            }

            case ILOpCode.Ldelem_i1: case ILOpCode.Ldelem_u1: case ILOpCode.Ldelem_i2: case ILOpCode.Ldelem_u2:
            case ILOpCode.Ldelem_i4: case ILOpCode.Ldelem_u4: case ILOpCode.Ldelem_i8: case ILOpCode.Ldelem_i:
            case ILOpCode.Ldelem_r4: case ILOpCode.Ldelem_r8:
            {
                var idx = Pop(stack);
                var arr = Pop(stack);
                Push(stack, new Expr($"{arr.P(Expr.Primary)}[{idx.Text}]", Expr.Primary, Hint.Number), res);
                return false;
            }
            case ILOpCode.Ldelem_ref:
            case ILOpCode.Ldelem:
            {
                var idx = Pop(stack);
                var arr = Pop(stack);
                var hint = code == ILOpCode.Ldelem ? Expr.HintOf(TypeOperand(ins)) : Hint.Reference;
                Push(stack, new Expr($"{arr.P(Expr.Primary)}[{idx.Text}]", Expr.Primary, hint), res);
                return false;
            }
            case ILOpCode.Ldelema:
            {
                var idx = Pop(stack);
                var arr = Pop(stack);
                var target = $"{arr.P(Expr.Primary)}[{idx.Text}]";
                Push(stack, new Expr("ref " + target, Expr.Unary) { AddressOf = target }, res);
                return false;
            }
            case ILOpCode.Stelem_i: case ILOpCode.Stelem_i1: case ILOpCode.Stelem_i2: case ILOpCode.Stelem_i4:
            case ILOpCode.Stelem_i8: case ILOpCode.Stelem_r4: case ILOpCode.Stelem_r8: case ILOpCode.Stelem_ref:
            case ILOpCode.Stelem:
            {
                var v = Pop(stack);
                var idx = Pop(stack);
                var arr = Pop(stack);
                res.Statements.Add($"{arr.P(Expr.Primary)}[{idx.Text}] = {v.Text};");
                return false;
            }
            case ILOpCode.Ldlen:
            {
                var arr = Pop(stack);
                Push(stack, new Expr(arr.P(Expr.Primary) + ".Length", Expr.Primary, Hint.Number), res);
                return false;
            }
            case ILOpCode.Newarr:
            {
                var n = Pop(stack);
                var t = TypeOperand(ins);
                int bracket = t.IndexOf('[');
                var text = bracket < 0 ? $"new {t}[{n.Text}]" : $"new {t.Substring(0, bracket)}[{n.Text}]{t.Substring(bracket)}";
                Push(stack, new Expr(text, Expr.Primary, Hint.Reference), res);
                return false;
            }

            case ILOpCode.Ldind_i1: case ILOpCode.Ldind_u1: case ILOpCode.Ldind_i2: case ILOpCode.Ldind_u2:
            case ILOpCode.Ldind_i4: case ILOpCode.Ldind_u4: case ILOpCode.Ldind_i8: case ILOpCode.Ldind_i:
            case ILOpCode.Ldind_r4: case ILOpCode.Ldind_r8:
                Push(stack, new Expr(Deref(Pop(stack)), Expr.Unary, Hint.Number), res);
                return false;
            case ILOpCode.Ldind_ref:
                Push(stack, new Expr(Deref(Pop(stack)), Expr.Unary, Hint.Reference), res);
                return false;
            case ILOpCode.Ldobj:
                Push(stack, new Expr(Deref(Pop(stack)), Expr.Unary, Expr.HintOf(TypeOperand(ins))), res);
                return false;
            case ILOpCode.Stind_i: case ILOpCode.Stind_i1: case ILOpCode.Stind_i2: case ILOpCode.Stind_i4:
            case ILOpCode.Stind_i8: case ILOpCode.Stind_r4: case ILOpCode.Stind_r8: case ILOpCode.Stind_ref:
            case ILOpCode.Stobj:
            {
                var v = Pop(stack);
                var p = Pop(stack);
                res.Statements.Add($"{Deref(p)} = {v.Text};");
                return false;
            }
            case ILOpCode.Cpobj:
            {
                var src = Pop(stack);
                var dst = Pop(stack);
                res.Statements.Add($"{Deref(dst)} = {Deref(src)};");
                return false;
            }
            case ILOpCode.Initobj:
                res.Statements.Add($"{Deref(Pop(stack))} = default({TypeOperand(ins)});");
                return false;
            case ILOpCode.Sizeof:
                Push(stack, Literal($"sizeof({TypeOperand(ins)})", Hint.Number), res);
                return false;
            case ILOpCode.Localloc:
                Push(stack, new Expr($"stackalloc byte[{Pop(stack).Text}]", Expr.Primary), res);
                return false;
            case ILOpCode.Ldtoken:
                Push(stack, Literal("token(" + TokenText(ins) + ")", Hint.Unknown), res);
                return false;
            case ILOpCode.Ldftn:
            {
                var m = _r.ResolveMethod(ins.Handle);
                Push(stack, new Expr("&" + m.ShortDeclaringType + "." + m.Name, Expr.Unary), res);
                return false;
            }
            case ILOpCode.Ldvirtftn:
            {
                var m = _r.ResolveMethod(ins.Handle);
                var o = Pop(stack);
                Push(stack, new Expr("&" + Receiver(o) + "." + m.Name, Expr.Unary), res);
                return false;
            }
            case ILOpCode.Endfinally:
                stack.Clear();
                res.Statements.Add("endfinally;");
                return false;
            case ILOpCode.Endfilter:
                res.Statements.Add("endfilter " + Pop(stack).Text + ";");
                return false;
            case ILOpCode.Ckfinite:
                Push(stack, new Expr($"ckfinite({Pop(stack).Text})", Expr.Primary, Hint.Number), res);
                return false;
            case ILOpCode.Arglist:
                Push(stack, Literal("__arglist", Hint.Unknown), res);
                return false;
            case ILOpCode.Mkrefany:
                Push(stack, new Expr($"__makeref({Deref(Pop(stack))})", Expr.Primary), res);
                return false;
            case ILOpCode.Refanyval:
                Push(stack, new Expr($"__refvalue({Pop(stack).Text}, {TypeOperand(ins)})", Expr.Primary), res);
                return false;
            case ILOpCode.Refanytype:
                Push(stack, new Expr($"__reftype({Pop(stack).Text})", Expr.Primary, Hint.Reference), res);
                return false;
            case ILOpCode.Cpblk:
            case ILOpCode.Initblk:
            {
                var args = PopN(stack, 3);
                res.Statements.Add($"{ins.Name}({string.Join(", ", args)});");
                return false;
            }
            default:
                return Generic(ins, stack, res);
        }
    }

    private string TokenText(ILInstruction ins) => ins.Handle.Kind switch
    {
        HandleKind.MethodDefinition or HandleKind.MemberReference when IsMethodToken(ins.Handle) => _r.ResolveMethod(ins.Handle).Display,
        HandleKind.FieldDefinition or HandleKind.MemberReference => _r.ResolveField(ins.Handle).DeclaringType + "." + _r.ResolveField(ins.Handle).Name,
        HandleKind.MethodSpecification => _r.ResolveMethod(ins.Handle).Display,
        _ => TypeOperand(ins),
    };

    private bool IsMethodToken(EntityHandle h)
    {
        if (h.Kind == HandleKind.MethodDefinition) return true;
        if (h.Kind != HandleKind.MemberReference) return false;
        return _r.Metadata.GetMemberReference((MemberReferenceHandle)h).GetKind() == MemberReferenceKind.Method;
    }

    private static bool Branch(List<Expr> stack, Result res, string op)
    {
        var b = Pop(stack);
        var a = Pop(stack);
        res.Condition = Compare(a, b, op);
        return false;
    }

    private bool CompareOp(List<Expr> stack, Result res, string op)
    {
        var b = Pop(stack);
        var a = Pop(stack);
        Push(stack, Compare(a, b, op), res);
        return false;
    }

    private bool Binary(List<Expr> stack, Result res, string op, int prec, bool @checked = false)
    {
        var b = Pop(stack);
        var a = Pop(stack);
        var text = $"{a.P(prec)} {op} {b.P(prec + 1)}";
        var hint = (op is "&" or "|" or "^") && a.Hint == Hint.Bool && b.Hint == Hint.Bool ? Hint.Bool : Hint.Number;
        Push(stack, @checked ? new Expr($"checked({text})", Expr.Primary, hint) : new Expr(text, prec, hint), res);
        return false;
    }

    private bool Cast(List<Expr> stack, Result res, string type, bool @checked = false)
    {
        var v = Pop(stack);
        var text = $"({type}){v.P(Expr.Unary)}";
        Push(stack, @checked ? new Expr($"checked({text})", Expr.Primary, Expr.HintOf(type)) : new Expr(text, Expr.Unary, Expr.HintOf(type)), res);
        return false;
    }

    private static readonly Dictionary<string, (string Op, int Prec)> BinaryOperators = new()
    {
        ["op_Equality"] = ("==", Expr.Equality), ["op_Inequality"] = ("!=", Expr.Equality),
        ["op_LessThan"] = ("<", Expr.Relational), ["op_GreaterThan"] = (">", Expr.Relational),
        ["op_LessThanOrEqual"] = ("<=", Expr.Relational), ["op_GreaterThanOrEqual"] = (">=", Expr.Relational),
        ["op_Addition"] = ("+", Expr.Add), ["op_Subtraction"] = ("-", Expr.Add),
        ["op_Multiply"] = ("*", Expr.Mult), ["op_Division"] = ("/", Expr.Mult), ["op_Modulus"] = ("%", Expr.Mult),
        ["op_BitwiseAnd"] = ("&", Expr.BitAnd), ["op_BitwiseOr"] = ("|", Expr.BitOr), ["op_ExclusiveOr"] = ("^", Expr.Xor),
        ["op_LeftShift"] = ("<<", Expr.Shift), ["op_RightShift"] = (">>", Expr.Shift),
    };

    private bool Call(ILInstruction ins, ILInstruction? next, List<Expr> stack, Result res, bool isNew)
    {
        var t = _r.ResolveMethod(ins.Handle);
        var args = PopN(stack, t.ParameterCount);
        Expr? obj = !isNew && t.HasThis ? Pop(stack) : null;
        var argText = string.Join(", ", args.Select(a => a.Text));
        var recv = obj != null ? Receiver(obj) : t.ShortDeclaringType;
        var name = t.Name;
        Expr? value = null;
        string? statement = null;

        if (isNew)
        {
            value = new Expr($"new {t.ShortDeclaringType}({argText})", Expr.Primary, Hint.Reference);
        }
        else if (name == ".ctor")
        {
            if (obj?.AddressOf != null) statement = $"{obj.AddressOf} = new {t.ShortDeclaringType}({argText});";
            else if (obj?.Text == "this") statement = (t.DeclaringType == Names.StripArity(_il.Method.TypeName).Replace('+', '.') ? "this" : "base") + $"({argText});";
            else statement = $"{recv}..ctor({argText});";
        }
        else if (name.StartsWith("get_", StringComparison.Ordinal) && args.Count == 0)
        {
            value = new Expr($"{recv}.{name.Substring(4)}", Expr.Primary, Expr.HintOf(t.ReturnType));
        }
        else if (name == "get_Item" && args.Count > 0 && obj != null)
        {
            value = new Expr($"{recv}[{argText}]", Expr.Primary, Expr.HintOf(t.ReturnType));
        }
        else if (name.StartsWith("set_", StringComparison.Ordinal) && args.Count == 1 && t.ReturnsVoid)
        {
            statement = $"{recv}.{name.Substring(4)} = {args[0].Text};";
        }
        else if (name == "set_Item" && args.Count >= 2 && obj != null && t.ReturnsVoid)
        {
            statement = $"{recv}[{string.Join(", ", args.Take(args.Count - 1).Select(a => a.Text))}] = {args[^1].Text};";
        }
        else if (obj == null && args.Count == 2 && BinaryOperators.TryGetValue(name, out var bin))
        {
            value = bin.Op is "==" or "!=" or "<" or ">" or "<=" or ">="
                ? Compare(args[0], args[1], bin.Op)
                : new Expr($"{args[0].P(bin.Prec)} {bin.Op} {args[1].P(bin.Prec + 1)}", bin.Prec, Expr.HintOf(t.ReturnType));
        }
        else if (obj == null && args.Count == 1 && name is "op_Implicit" or "op_Explicit")
        {
            value = new Expr($"({t.ReturnType}){args[0].P(Expr.Unary)}", Expr.Unary, Expr.HintOf(t.ReturnType));
        }
        else if (obj == null && args.Count == 1 && name is "op_LogicalNot" or "op_UnaryNegation" or "op_OnesComplement")
        {
            var op = name == "op_LogicalNot" ? "!" : name == "op_UnaryNegation" ? "-" : "~";
            value = new Expr(op + args[0].P(Expr.Unary), Expr.Unary, Expr.HintOf(t.ReturnType));
        }
        else
        {
            value = new Expr($"{recv}.{name}({argText})", Expr.Primary, Expr.HintOf(t.ReturnType));
        }

        if (statement != null)
        {
            res.Statements.Add(statement);
            return false;
        }
        if (t.ReturnsVoid && !isNew)
        {
            res.Statements.Add(value!.Text + ";");
            return false;
        }
        // A result that is immediately discarded becomes a statement.
        if (next != null && next.OpCode.Value == OpCodes.Pop.Value)
        {
            res.Statements.Add(value!.Text + ";");
            return true;
        }
        Push(stack, value!, res);
        return false;
    }

    private bool Calli(ILInstruction ins, ILInstruction? next, List<Expr> stack, Result res)
    {
        MethodSignature<string> sig;
        try
        {
            _r.ResolveStandaloneSignature((StandaloneSignatureHandle)ins.Handle, out sig);
        }
        catch (Exception)
        {
            res.Statements.Add("calli ?;");
            return false;
        }
        var fptr = Pop(stack);
        var args = PopN(stack, sig.ParameterTypes.Length);
        if (sig.Header.IsInstance) args.Insert(0, Pop(stack));
        var text = $"(({fptr.Text}))({string.Join(", ", args)})";
        if (sig.ReturnType == "void")
        {
            res.Statements.Add(text + ";");
            return false;
        }
        if (next != null && next.OpCode.Value == OpCodes.Pop.Value)
        {
            res.Statements.Add(text + ";");
            return true;
        }
        Push(stack, new Expr(text, Expr.Primary, Expr.HintOf(sig.ReturnType)), res);
        return false;
    }

    /// <summary>Fallback for rare opcodes: uses the stack behaviour from the opcode table.</summary>
    private bool Generic(ILInstruction ins, List<Expr> stack, Result res)
    {
        var (pop, push) = StackEffect.Of(ins, _r, _il.ReturnType);
        var args = PopN(stack, Math.Max(0, pop));
        var text = $"{ins.Name}({string.Join(", ", args)})";
        if (push == 0) res.Statements.Add(text + ";");
        else Push(stack, new Expr(text, Expr.Primary), res);
        return false;
    }
}

/// <summary>Number of values an instruction pops and pushes.</summary>
public static class StackEffect
{
    /// <summary>Pop count is -1 for instructions that empty the stack (leave, endfinally).</summary>
    public static (int Pop, int Push) Of(ILInstruction ins, MemberResolver resolver, string returnType)
    {
        var op = ins.OpCode;
        var code = (ILOpCode)(ushort)op.Value;
        switch (code)
        {
            case ILOpCode.Leave:
            case ILOpCode.Leave_s:
            case ILOpCode.Endfinally:
                return (-1, 0);
            case ILOpCode.Ret:
                return (returnType == "void" ? 0 : 1, 0);
            case ILOpCode.Call:
            case ILOpCode.Callvirt:
            case ILOpCode.Newobj:
            {
                var m = resolver.ResolveMethod(ins.Handle);
                bool isNew = code == ILOpCode.Newobj;
                int pop = m.ParameterCount + (m.HasThis && !isNew ? 1 : 0);
                int push = isNew || !m.ReturnsVoid ? 1 : 0;
                return (pop, push);
            }
            case ILOpCode.Calli:
            {
                try
                {
                    resolver.ResolveStandaloneSignature((StandaloneSignatureHandle)ins.Handle, out var sig);
                    return (sig.ParameterTypes.Length + (sig.Header.IsInstance ? 1 : 0) + 1, sig.ReturnType == "void" ? 0 : 1);
                }
                catch (Exception)
                {
                    return (1, 0);
                }
            }
        }

        int popCount = op.StackBehaviourPop switch
        {
            StackBehaviour.Pop0 => 0,
            StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
            StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8
                or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
            StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8
                or StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref
                or StackBehaviour.Popref_popi_pop1 => 3,
            _ => 0,
        };
        int pushCount = op.StackBehaviourPush switch
        {
            StackBehaviour.Push0 => 0,
            StackBehaviour.Push1_push1 => 2,
            StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or StackBehaviour.Pushr4
                or StackBehaviour.Pushr8 or StackBehaviour.Pushref => 1,
            _ => 0,
        };
        return (popCount, pushCount);
    }
}
