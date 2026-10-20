using System.Text;
using NetScope.Calls;
using NetScope.Cfg;
using NetScope.Symbols;

namespace NetScope.Reporting;

/// <summary>Human-readable hierarchy: assembly, type, method, then the selected groups.</summary>
public sealed class TextReporter : IReporter
{
    private readonly OutputGroups _groups;
    private readonly bool _color;
    private readonly SourceFiles? _sources;

    public TextReporter(OutputGroups groups, bool color, SourceFiles? sources = null)
    {
        _groups = groups;
        _color = color;
        _sources = sources;
    }

    public string Extension => "txt";

    private string C(string code, string text) => _color ? $"\u001b[{code}m{text}\u001b[0m" : text;
    private string Header(string text) => C("36", text);
    private string Strong(string text) => C("1;33", text);
    private string Dim(string text) => C("90", text);
    private string Bad(string text) => C("31", text);

    public string BeginDocument() => "";

    public string EndDocument() => "";

    public string RenderAssembly(AssemblyReport report, bool first)
    {
        var sb = new StringBuilder();
        var asm = report.Assembly;
        if (!first) sb.AppendLine();
        sb.AppendLine(Strong($"Assembly : {asm.FileName}"));
        sb.AppendLine($"Name     : {asm.Name}{(asm.Version != null ? ", Version=" + asm.Version : "")}");
        sb.AppendLine($"Path     : {asm.FilePath}");
        sb.AppendLine($"Runtime  : {asm.TargetFramework ?? asm.MetadataVersion}   Machine: {asm.Architecture}");
        foreach (var n in asm.Notes) sb.AppendLine($"Note     : {n}");
        foreach (var e in report.Errors) sb.AppendLine(Bad($"Error    : {e}"));
        if (report.Audit is { } audit)
            sb.AppendLine($"Audit    : {audit.Count(Audit.Severity.Error)} error(s), {audit.Count(Audit.Severity.Warning)} warning(s), {audit.Count(Audit.Severity.Note)} note(s), {audit.Suppressed} suppressed");
        if (report.Obfuscation is { } obf)
        {
            var who = obf.Obfuscator != null ? $" ({obf.Obfuscator})" : "";
            sb.AppendLine($"Obfuscated: {obf.VerdictText}{who}");
            foreach (var s in obf.Signs) sb.AppendLine($"  sign: {s.Kind}: {TextSafety.EscapeControl(s.Detail)}");
            if (obf.Hint != null) sb.AppendLine(Strong($"  hint: {obf.Hint}"));
        }
        if (report.Methods.Count > 0 || report.Obfuscation == null) sb.AppendLine($"Methods  : {report.Methods.Count}");

        string? currentType = null;
        foreach (var m in report.Methods)
        {
            if (m.Method.TypeName != currentType)
            {
                currentType = m.Method.TypeName;
                sb.AppendLine();
                sb.AppendLine(Header($"Type     : {currentType}"));
            }
            sb.AppendLine();
            RenderMethod(sb, m);
        }
        if (_groups.Strings) RenderStrings(sb, report);
        return sb.ToString();
    }

    private void RenderMethod(StringBuilder sb, MethodReport r)
    {
        var m = r.Method;
        sb.AppendLine(Strong($"Method   : {m.Signature}"));
        sb.AppendLine($"Kind     : {string.Join(", ", m.DisplayKinds())}");
        var size = r.CodeSize is int s ? $"   Size: {s} bytes" : "";
        sb.AppendLine($"RVA      : 0x{m.Rva:X4}{size}   Token: 0x{m.Token:X8}");
        if (m.PInvoke != null) sb.AppendLine($"PInvoke  : {m.PInvoke}");
        foreach (var e in r.Errors) sb.AppendLine(Bad($"Error    : {e}"));

        if (r.Findings != null) RenderFindings(sb, r.Findings);
        if (r.Matches != null) RenderMatches(sb, r.Matches);
        if (r.Calls != null) RenderCalls(sb, r);
        if (r.Fields != null) RenderFields(sb, r.Fields);
        if (r.Cfg != null) RenderCfg(sb, r);
        if (r.Pseudo != null)
        {
            sb.AppendLine();
            var source = r.Pseudo.Source == "decompiler" ? "" : Dim($"  (fallback: {r.Pseudo.Reason})");
            sb.AppendLine(Header("[Pseudocode]") + source);
            foreach (var line in SplitLines(r.Pseudo.Text)) sb.Append("  ").AppendLine(line);
        }
        if (r.IL != null)
        {
            sb.AppendLine();
            sb.AppendLine(Header("[IL]") + Dim($"  .maxstack {r.IL.MaxStack}"));
            if (r.IL.Locals.Count > 0)
                sb.AppendLine(Dim($"  .locals{(r.IL.LocalsInit ? " init" : "")} ({string.Join(", ", r.IL.Locals)})"));
            foreach (var l in r.IL.Lines)
            {
                if (l.StartsSource && l.Source != null) RenderSource(sb, l.Source);
                sb.AppendLine(l.Operand.Length == 0 ? $"  {l.Label}  {l.OpCode}" : $"  {l.Label}  {l.OpCode,-10} {l.Operand}");
            }
            foreach (var c in r.IL.ExceptionClauses) sb.AppendLine(Dim("  " + c));
        }
        if (r.Native != null)
        {
            sb.AppendLine();
            var n = r.Native;
            if (!n.Available)
            {
                sb.AppendLine(Header("[Native]") + Dim("  " + n.Reason));
            }
            else
            {
                var info = string.Join(", ", new[] { n.Architecture, n.Source, n.Tier, n.RuntimeVersion, $"{n.Size} bytes" }.Where(x => !string.IsNullOrEmpty(x)));
                sb.AppendLine(Header("[Native]") + Dim("  " + info));
                bool showParts = n.Parts.Count > 1 || n.Parts.Any(p => p.Name != "main");
                string? part = null;
                foreach (var l in n.Lines)
                {
                    if (showParts && l.Part != part)
                    {
                        part = l.Part;
                        var p = n.Parts.FirstOrDefault(x => x.Name == part);
                        sb.AppendLine(Dim($"  ; {part}" + (p != null ? $"  (0x{p.Address:X8}, {p.Size} bytes)" : "")));
                    }
                    if (n.Source == "JIT") sb.Append("  ").AppendLine(l.Text);
                    else sb.AppendLine($"  {l.Address:X8}  {l.Bytes,-20} {l.Text}");
                }
            }
        }
    }

    /// <summary>"// Program.cs:42" before the first instruction of a sequence point, then the source text with --source.</summary>
    private void RenderSource(StringBuilder sb, SourcePoint p)
    {
        sb.AppendLine(Dim($"  // {p.File}:{p.StartLine}"));
        var lines = _sources?.Lines(p.Document, p.StartLine, p.EndLine);
        if (lines == null) return;
        for (int i = 0; i < lines.Count; i++)
            sb.AppendLine(Dim($"  // {p.StartLine + i,5} | {TextSafety.EscapeControl(lines[i].TrimEnd())}"));
    }

    private void RenderFindings(StringBuilder sb, IReadOnlyList<Audit.Finding> findings)
    {
        sb.AppendLine();
        sb.AppendLine(Header("[Audit]"));
        foreach (var f in findings)
        {
            var level = f.Level.ToString().ToLowerInvariant();
            var levelText = f.Level == Audit.Severity.Error ? Bad($"{level,-8}") : $"{level,-8}";
            var at = f.Offset is int off ? $"IL_{off:X4}" : "decl.  ";
            var source = f.Source != null ? Dim($"  // {f.Source.File}:{f.Source.StartLine}") : "";
            sb.AppendLine($"  {f.RuleId}  {levelText} {at}  {f.Message}{source}");
        }
    }

    /// <summary>Why --uses, --find-string or --field-refs selected the method.</summary>
    private void RenderMatches(StringBuilder sb, IReadOnlyList<CrossRefs.CrossReference> matches)
    {
        sb.AppendLine();
        sb.AppendLine(Header("[Matches]"));
        var shown = matches.Select(x => (x, Target: x.Query == "string" ? Metadata.Names.EscapeString(x.Target) : x.Target)).ToList();
        int width = shown.Max(x => x.Target.Length);
        foreach (var (x, target) in shown)
        {
            var source = x.Source != null ? Dim("  // " + x.Source.File + ":" + x.Source.StartLine) : "";
            sb.AppendLine(source.Length == 0 ? $"  IL_{x.Offset:X4}  {x.Kind,-9} {target}" : $"  IL_{x.Offset:X4}  {x.Kind,-9} {target.PadRight(width)}{source}");
        }
    }

    /// <summary>Fields grouped by access kind, each with the offsets where it is used.</summary>
    private void RenderFields(StringBuilder sb, IReadOnlyList<CrossRefs.FieldAccess> fields)
    {
        sb.AppendLine();
        sb.AppendLine(Header("[Fields]"));
        if (fields.Count == 0)
        {
            sb.AppendLine(Dim("  (none)"));
            return;
        }
        var groups = fields.GroupBy(f => (f.Access, f.FullName, f.Field.FieldType)).ToList();
        int width = groups.Max(g => g.Key.FullName.Length);
        foreach (var g in groups)
            sb.AppendLine($"  {g.Key.Access,-8} {g.Key.FullName.PadRight(width)}  {g.Key.FieldType}  " + Dim(string.Join(" ", g.Select(f => $"IL_{f.Offset:X4}"))));
    }

    private void RenderStrings(StringBuilder sb, AssemblyReport report)
    {
        sb.AppendLine();
        sb.AppendLine(Header("[Strings]") + $"  count={report.StringIndex.Count}");
        var rows = report.StringIndex.Values.OrderBy(v => v.Value, StringComparer.Ordinal)
            .Select(s => (Text: Metadata.Names.EscapeString(CrossRefs.CrossReferenceQuery.Clip(s.Value)), s.Locations)).ToList();
        int width = rows.Count == 0 ? 0 : Math.Min(60, rows.Max(r => r.Text.Length));
        foreach (var (text, locations) in rows)
        {
            var where = string.Join(", ", locations.Select(l => $"{l.Method.FullName} IL_{l.Offset:X4}"));
            sb.AppendLine($"  {text.PadRight(width)}  " + Dim(where));
        }
    }

    private void RenderCalls(StringBuilder sb, MethodReport r)
    {
        sb.AppendLine();
        sb.AppendLine(Header("[Calls]"));
        if (r.Calls!.Count == 0) sb.AppendLine(Dim("  (none)"));
        int width = r.Calls.Count == 0 ? 0 : r.Calls.Max(c => c.Site.Display.Length);
        foreach (var c in r.Calls)
        {
            var scope = c.Site.Scope;
            if (!c.Site.IsInternal && c.Site.Target?.Assembly != null) scope += " " + c.Site.Target.Assembly;
            if (c.GroupTarget != null && c.GroupTarget.Assembly != r.Method.Assembly) scope += $" (in group: {c.GroupTarget.Assembly.FileName})";
            sb.AppendLine($"  IL_{c.Site.Offset:X4}  {c.Site.Kind,-9} {c.Site.Display.PadRight(width)}  {scope}");
        }
        if (r.Callers != null)
        {
            sb.AppendLine(Header("[Callers]"));
            if (r.Callers.Count == 0) sb.AppendLine(Dim("  (none in the input files)"));
            foreach (var c in r.Callers)
                sb.AppendLine($"  {c.Caller.FullName}  IL_{c.Offset:X4}  {c.Kind}  ({c.Caller.Assembly.FileName})");
        }
    }

    private void RenderCfg(StringBuilder sb, MethodReport report)
    {
        var cfg = report.Cfg!;
        sb.AppendLine();
        sb.AppendLine(Header("[Branches]") + $"  complexity={cfg.CyclomaticComplexity}  blocks={cfg.Blocks.Count}  loops={cfg.Loops.Count}");
        foreach (var b in cfg.Blocks)
        {
            var flags = new List<string>();
            if (b.IsLoopHeader) flags.Add("loop");
            if (!b.Reachable) flags.Add("unreachable");
            if (b.Region != null) flags.Add(b.Region.Title);
            var flagText = flags.Count > 0 ? Dim("  [" + string.Join(", ", flags) + "]") : "";
            var source = report.BlockSources != null && report.BlockSources.TryGetValue(b.Index, out var range) ? Dim("  // " + range.Label) : "";
            sb.AppendLine($"  {b.Name} IL_{b.StartOffset:X4}..IL_{b.EndOffset:X4}  -> {Successors(b)}{flagText}{source}");
            foreach (var ins in b.Instructions)
                sb.AppendLine(Dim($"       IL_{ins.Offset:X4}  {report.Instruction(ins)}"));
        }
        foreach (var l in cfg.Loops)
            sb.AppendLine($"  loop: header {l.Header.Name}, back edges from {string.Join(", ", l.Latches.Select(x => x.Name))}, body {string.Join(" ", l.Body.Select(x => x.Name))}");
        var unreachable = cfg.Unreachable.ToList();
        if (unreachable.Count > 0) sb.AppendLine($"  unreachable: {string.Join(", ", unreachable.Select(x => x.Name))}");
        var exits = cfg.Exits.ToList();
        if (exits.Count > 0) sb.AppendLine($"  exits: {string.Join(", ", exits.Select(x => $"{x.Name} ({x.ExitKind})"))}");
        if (cfg.Regions.Count > 0)
        {
            sb.AppendLine("  regions:");
            foreach (var r in cfg.Regions) RenderRegion(sb, r, 2);
        }
        foreach (var w in cfg.Warnings) sb.AppendLine(Bad("  warning: " + w));
    }

    private static void RenderRegion(StringBuilder sb, ExceptionRegionNode r, int depth)
    {
        var blocks = r.Blocks.Count == 0 ? "" : $"  [{string.Join(" ", r.Blocks.Select(b => b.Name))}]";
        var ofTry = r.Try != null ? $"  (handler of try IL_{r.Try.Start:X4})" : "";
        sb.Append(new string(' ', depth * 2)).AppendLine($"{r.Title} IL_{r.Start:X4}..IL_{r.End - 1:X4}{blocks}{ofTry}");
        foreach (var c in r.Children) RenderRegion(sb, c, depth + 1);
    }

    /// <summary>"B1 (user != null) | B3 (else)", "exit", "B2 (case 0) | B4 (default)".</summary>
    private static string Successors(BasicBlock b)
    {
        var parts = new List<string>();
        var conditional = b.Successors.Where(e => e.Kind is EdgeKind.True or EdgeKind.False).OrderBy(e => e.To.Index).ToList();
        for (int i = 0; i < conditional.Count; i++)
        {
            var e = conditional[i];
            parts.Add(i == 0 ? $"{e.To.Name} ({e.Condition ?? e.KindName})" : $"{e.To.Name} (else)");
        }
        foreach (var e in b.Successors)
        {
            switch (e.Kind)
            {
                case EdgeKind.Jump:
                case EdgeKind.Fallthrough:
                    parts.Add(e.To.Name + (e.IsBackEdge ? " (loop)" : ""));
                    break;
                case EdgeKind.Case:
                    parts.Add($"{e.To.Name} (case {e.CaseIndex})");
                    break;
                case EdgeKind.Default:
                    parts.Add($"{e.To.Name} (default)");
                    break;
                case EdgeKind.Leave:
                    parts.Add($"{e.To.Name} (leave)");
                    break;
                case EdgeKind.Exception:
                    parts.Add($"{e.To.Name} ({e.Condition})");
                    break;
            }
        }
        if (b.ExitKind != null) parts.Add(b.ExitKind == "return" ? "exit" : b.ExitKind);
        return parts.Count == 0 ? "(none)" : string.Join(" | ", parts);
    }

    public string RenderCallGraph(IReadOnlyList<CallGraphEdge> edges, bool afterAssemblies)
    {
        var sb = new StringBuilder();
        if (afterAssemblies) sb.AppendLine();
        sb.AppendLine(Strong("[CallGraph]") + $"  edges={edges.Count}");
        foreach (var e in edges)
        {
            var kinds = string.Join(",", e.Sites.Select(s => s.Kind).Distinct());
            var count = e.Sites.Count > 1 ? $" x{e.Sites.Count}" : "";
            sb.AppendLine($"  {e.Caller.Assembly.Name}!{e.Caller.FullName} -> {e.Callee.Assembly.Name}!{e.Callee.FullName}  ({kinds}{count})");
        }
        return sb.ToString();
    }

    internal static IEnumerable<string> SplitLines(string text) => text.Replace("\r\n", "\n").Split('\n');
}
