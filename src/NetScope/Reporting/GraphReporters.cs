using System.Text;
using NetScope.Calls;
using NetScope.Cfg;
using static NetScope.Reporting.GraphText;

namespace NetScope.Reporting;

internal static class GraphText
{
    public static string Clip(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 3) + "...";
}

/// <summary>Graphviz output: one digraph per method CFG, plus the call graph.</summary>
public sealed class DotReporter : IReporter
{
    private readonly bool _cfg;

    public DotReporter(bool cfg) => _cfg = cfg;

    public string Extension => "dot";
    public string BeginDocument() => "";
    public string EndDocument() => "";

    private static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";

    /// <summary>Left-aligned multi-line label.</summary>
    private static string L(IEnumerable<string> lines) =>
        "\"" + string.Concat(lines.Select(l => l.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("{", "\\{").Replace("}", "\\}") + "\\l")) + "\"";

    public string RenderAssembly(AssemblyReport report, bool first)
    {
        if (!_cfg) return "";
        var sb = new StringBuilder();
        foreach (var m in report.Methods)
        {
            if (m.Cfg == null) continue;
            var name = $"{report.Assembly.Name}!{m.Method.FullName}";
            sb.AppendLine($"digraph {Q(name + " 0x" + m.Method.Token.ToString("X8"))} {{");
            sb.AppendLine($"  label={Q(name + "\n" + m.Method.Signature + "\ncomplexity=" + m.Cfg.CyclomaticComplexity)};");
            sb.AppendLine("  labelloc=t;");
            sb.AppendLine("  node [shape=box, fontname=\"Consolas\", fontsize=10];");
            sb.AppendLine("  edge [fontname=\"Consolas\", fontsize=9];");
            foreach (var r in m.Cfg.Regions) RenderRegion(sb, r, "  ", m);
            foreach (var b in m.Cfg.Blocks.Where(b => b.Region == null)) sb.AppendLine("  " + Node(b, m));
            bool hasExit = false;
            foreach (var b in m.Cfg.Blocks)
            {
                foreach (var e in b.Successors)
                {
                    var attrs = new List<string>();
                    if (e.Label.Length > 0) attrs.Add("label=" + Q(e.Label));
                    if (e.Kind == EdgeKind.Exception) attrs.Add("style=dashed");
                    if (e.IsBackEdge) attrs.Add("color=red");
                    if (e.Kind == EdgeKind.False) attrs.Add("color=gray40");
                    sb.AppendLine($"  {b.Name} -> {e.To.Name}{(attrs.Count > 0 ? " [" + string.Join(", ", attrs) + "]" : "")};");
                }
                if (b.ExitKind is "return" or "throw" or "rethrow" or "jmp")
                {
                    hasExit = true;
                    sb.AppendLine($"  {b.Name} -> exit{(b.ExitKind != "return" ? " [label=" + Q(b.ExitKind) + ", style=dotted]" : "")};");
                }
            }
            if (hasExit) sb.AppendLine("  exit [shape=oval];");
            sb.AppendLine("}");
        }
        return sb.ToString();
    }

    private static string Node(BasicBlock b, MethodReport m)
    {
        var lines = new List<string> { $"{b.Name}  IL_{b.StartOffset:X4}..IL_{b.EndOffset:X4}" };
        lines.AddRange(b.Instructions.Select(i => Clip($"IL_{i.Offset:X4}: {m.Instruction(i)}", 70)));
        var style = !b.Reachable ? ", style=filled, fillcolor=gray90" : b.IsLoopHeader ? ", penwidth=2" : "";
        return $"{b.Name} [label={L(lines)}{style}];";
    }

    private static void RenderRegion(StringBuilder sb, ExceptionRegionNode r, string indent, MethodReport m)
    {
        sb.AppendLine($"{indent}subgraph cluster_r{r.Index} {{");
        sb.AppendLine($"{indent}  label={Q(r.Title)}; style=dashed;");
        foreach (var b in r.Blocks) sb.AppendLine($"{indent}  {Node(b, m)}");
        foreach (var c in r.Children) RenderRegion(sb, c, indent + "  ", m);
        sb.AppendLine($"{indent}}}");
    }

    public string RenderCallGraph(IReadOnlyList<CallGraphEdge> edges, bool afterAssemblies)
    {
        var ids = CallGraphNodes.Assign(edges);
        var sb = new StringBuilder();
        sb.AppendLine("digraph callgraph {");
        sb.AppendLine("  rankdir=LR;");
        sb.AppendLine("  node [shape=box, fontname=\"Consolas\", fontsize=10];");
        int cluster = 0;
        foreach (var group in ids.GroupBy(kv => kv.Key.Assembly))
        {
            sb.AppendLine($"  subgraph cluster_a{cluster++} {{");
            sb.AppendLine($"    label={Q(group.Key.FileName)};");
            foreach (var (m, id) in group) sb.AppendLine($"    {id} [label={Q(m.FullName)}];");
            sb.AppendLine("  }");
        }
        foreach (var e in edges)
        {
            var label = string.Join(",", e.Sites.Select(s => s.Kind).Distinct()) + (e.Sites.Count > 1 ? " x" + e.Sites.Count : "");
            sb.AppendLine($"  {ids[e.Caller]} -> {ids[e.Callee]} [label={Q(label)}];");
        }
        sb.AppendLine("}");
        return sb.ToString();
    }
}

/// <summary>Mermaid flowcharts in Markdown fences, ready to paste into documentation.</summary>
public sealed class MermaidReporter : IReporter
{
    private readonly bool _cfg;

    public MermaidReporter(bool cfg) => _cfg = cfg;

    public string Extension => "md";
    public string BeginDocument() => "";
    public string EndDocument() => "";

    private static string E(string s) => s
        .Replace("&", "#amp;").Replace("\"", "#quot;").Replace("<", "#lt;").Replace(">", "#gt;")
        .Replace("|", "#124;").Replace("[", "#91;").Replace("]", "#93;").Replace("{", "#123;").Replace("}", "#125;");

    public string RenderAssembly(AssemblyReport report, bool first)
    {
        if (!_cfg) return "";
        var sb = new StringBuilder();
        foreach (var m in report.Methods)
        {
            if (m.Cfg == null) continue;
            sb.AppendLine($"## {report.Assembly.Name}!{m.Method.FullName}");
            sb.AppendLine();
            sb.AppendLine($"`{m.Method.Signature}` — complexity {m.Cfg.CyclomaticComplexity}");
            sb.AppendLine();
            sb.AppendLine("```mermaid");
            sb.AppendLine("flowchart TD");
            foreach (var r in m.Cfg.Regions) RenderRegion(sb, r, "  ", m);
            foreach (var b in m.Cfg.Blocks.Where(b => b.Region == null)) sb.AppendLine("  " + Node(b, m));
            bool hasExit = false;
            foreach (var b in m.Cfg.Blocks)
            {
                foreach (var e in b.Successors)
                {
                    var arrow = e.Kind == EdgeKind.Exception ? "-.->" : e.IsBackEdge ? "==>" : "-->";
                    sb.AppendLine(e.Label.Length > 0 ? $"  {b.Name} {arrow}|\"{E(e.Label)}\"| {e.To.Name}" : $"  {b.Name} {arrow} {e.To.Name}");
                }
                if (b.ExitKind is "return" or "throw" or "rethrow" or "jmp")
                {
                    hasExit = true;
                    sb.AppendLine(b.ExitKind == "return" ? $"  {b.Name} --> exit" : $"  {b.Name} -.->|{b.ExitKind}| exit");
                }
            }
            if (hasExit) sb.AppendLine("  exit((exit))");
            sb.AppendLine("```");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string Node(BasicBlock b, MethodReport m)
    {
        var lines = new List<string> { $"{b.Name} IL_{b.StartOffset:X4}..IL_{b.EndOffset:X4}" };
        lines.AddRange(b.Instructions.Select(i => Clip(m.Instruction(i), 50)));
        return $"{b.Name}[\"{string.Join("<br/>", lines.Select(E))}\"]";
    }

    private static void RenderRegion(StringBuilder sb, ExceptionRegionNode r, string indent, MethodReport m)
    {
        sb.AppendLine($"{indent}subgraph r{r.Index} [\"{E(r.Title)}\"]");
        foreach (var b in r.Blocks) sb.AppendLine($"{indent}  {Node(b, m)}");
        foreach (var c in r.Children) RenderRegion(sb, c, indent + "  ", m);
        sb.AppendLine($"{indent}end");
    }

    public string RenderCallGraph(IReadOnlyList<CallGraphEdge> edges, bool afterAssemblies)
    {
        var ids = CallGraphNodes.Assign(edges);
        var sb = new StringBuilder();
        sb.AppendLine("## Call graph");
        sb.AppendLine();
        sb.AppendLine("```mermaid");
        sb.AppendLine("flowchart LR");
        int cluster = 0;
        foreach (var group in ids.GroupBy(kv => kv.Key.Assembly))
        {
            sb.AppendLine($"  subgraph a{cluster++} [\"{E(group.Key.FileName)}\"]");
            foreach (var (m, id) in group) sb.AppendLine($"    {id}[\"{E(m.FullName)}\"]");
            sb.AppendLine("  end");
        }
        foreach (var e in edges)
        {
            var label = string.Join(",", e.Sites.Select(s => s.Kind).Distinct()) + (e.Sites.Count > 1 ? " x" + e.Sites.Count : "");
            sb.AppendLine($"  {ids[e.Caller]} -->|{E(label)}| {ids[e.Callee]}");
        }
        sb.AppendLine("```");
        return sb.ToString();
    }
}
