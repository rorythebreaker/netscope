using System.Text;
using NetScope.Cfg;
using NetScope.IL;
using NetScope.Metadata;

namespace NetScope.Pseudo;

/// <summary>Simplified three-address pseudocode built from IL and the CFG (used when decompilation fails).</summary>
public static class FallbackPseudo
{
    public static string Render(MethodIL il, ControlFlowGraph cfg, MemberResolver resolver)
    {
        var sb = new StringBuilder();
        sb.AppendLine(il.Method.Signature);
        sb.AppendLine("{");
        for (int i = 0; i < il.LocalTypes.Length; i++)
            sb.Append("    ").Append(il.LocalTypes[i]).Append(' ').Append(il.LocalName(i)).AppendLine(";");

        var evaluator = new SymbolicEvaluator(il, resolver, threeAddress: true);
        var open = new Stack<ExceptionRegionNode>();
        string Indent() => new(' ', 4 * (open.Count + 1));

        foreach (var b in cfg.Blocks)
        {
            while (open.Count > 0 && !open.Peek().Contains(b.StartOffset))
            {
                open.Pop();
                sb.Append(Indent()).AppendLine("}");
            }
            var chain = new List<ExceptionRegionNode>();
            for (var r = b.Region; r != null; r = r.Parent) chain.Add(r);
            chain.Reverse();
            foreach (var r in chain)
            {
                if (open.Contains(r)) continue;
                sb.Append(Indent()).AppendLine(RegionHeader(r) + " {");
                open.Push(r);
            }

            var label = new StringBuilder(b.Name + ":");
            if (b.IsLoopHeader) label.Append(" // loop header");
            if (!b.Reachable) label.Append(" // unreachable");
            sb.Append(new string(' ', Math.Max(0, 4 * open.Count))).AppendLine(label.ToString());

            var indent = Indent();
            SymbolicEvaluator.Result result;
            try
            {
                result = evaluator.Run(b.Instructions, CfgBuilder.EntryStack(b));
            }
            catch (Exception ex)
            {
                sb.Append(indent).AppendLine($"// cannot evaluate block: {ex.Message}");
                continue;
            }
            foreach (var s in result.Statements) sb.Append(indent).AppendLine(s);
            // Values left on the stack flow into the next block through slots s0, s1, ...
            for (int i = 0; i < result.ExitStack.Count; i++)
            {
                var v = result.ExitStack[i];
                if (v.Text != "s" + i) sb.Append(indent).AppendLine($"s{i} = {v.Text};");
            }
            AppendTerminator(sb, indent, b, result);
        }
        while (open.Count > 0)
        {
            open.Pop();
            sb.Append(Indent()).AppendLine("}");
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static string RegionHeader(ExceptionRegionNode r) => r.Kind switch
    {
        "catch" when r.CatchType == "when filter" => "catch (ex) when (filter)",
        "catch" => $"catch ({r.CatchType} ex)",
        _ => r.Kind,
    };

    private static void AppendTerminator(StringBuilder sb, string indent, BasicBlock b, SymbolicEvaluator.Result result)
    {
        var last = b.Last;
        string Target(EdgeKind kind) => b.Successors.FirstOrDefault(e => e.Kind == kind)?.To.Name ?? "?";
        if (last.IsConditionalBranch && result.Condition != null)
        {
            sb.Append(indent).AppendLine($"if ({SymbolicEvaluator.Truthy(result.Condition).Text}) goto {Target(EdgeKind.True)};");
        }
        else if (last.IsSwitch)
        {
            sb.Append(indent).AppendLine($"switch ({result.SwitchValue?.Text ?? "?"})");
            sb.Append(indent).AppendLine("{");
            foreach (var e in b.Successors.Where(e => e.Kind == EdgeKind.Case))
                sb.Append(indent).AppendLine($"    case {e.CaseIndex}: goto {e.To.Name};");
            sb.Append(indent).AppendLine("}");
        }
        else if (last.IsLeave)
        {
            sb.Append(indent).AppendLine($"leave {Target(EdgeKind.Leave)};");
        }
        else if (last.IsUnconditionalBranch)
        {
            sb.Append(indent).AppendLine($"goto {Target(EdgeKind.Jump)};");
        }
    }
}
