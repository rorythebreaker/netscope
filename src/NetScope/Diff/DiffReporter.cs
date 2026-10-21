using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using NetScope.Reporting;

namespace NetScope.Diff;

/// <summary>Text and JSON output of `netscope diff`. Unchanged methods are counted, not listed.</summary>
public static class DiffReporter
{
    public static string Text(DiffResult result, bool color)
    {
        string C(string code, string text) => color ? $"\u001b[{code}m{text}\u001b[0m" : text;
        var sb = new StringBuilder();
        foreach (var path in result.OnlyOld) sb.AppendLine(C("31", $"Removed  : {path}"));
        foreach (var path in result.OnlyNew) sb.AppendLine(C("32", $"Added    : {path}"));
        bool first = result.OnlyOld.Count == 0 && result.OnlyNew.Count == 0;
        foreach (var a in result.Assemblies)
        {
            if (!first) sb.AppendLine();
            first = false;
            sb.AppendLine(C("1;33", $"Assembly : {a.Name}"));
            sb.AppendLine($"Old      : {a.OldPath}");
            sb.AppendLine($"New      : {a.NewPath}");
            foreach (var e in a.Errors) sb.AppendLine(C("31", $"Error    : {e}"));
            sb.AppendLine($"Methods  : {a.Count(MethodStatus.Added)} added, {a.Count(MethodStatus.Removed)} removed, {a.Count(MethodStatus.Changed)} changed, {a.Count(MethodStatus.Unchanged)} unchanged");
            if (!a.HasDifferences && a.NewFindings.Count == 0) sb.AppendLine("Result   : no changes");

            List(sb, C, "[Added]", a.Methods.Where(m => m.Status == MethodStatus.Added));
            List(sb, C, "[Removed]", a.Methods.Where(m => m.Status == MethodStatus.Removed));
            foreach (var m in a.Methods.Where(m => m.Status == MethodStatus.Changed))
            {
                sb.AppendLine();
                var stats = new List<string>();
                if (m.OldComplexity != m.NewComplexity) stats.Add($"complexity {m.OldComplexity} -> {m.NewComplexity}");
                if (m.OldBlocks != m.NewBlocks) stats.Add($"blocks {m.OldBlocks} -> {m.NewBlocks}");
                sb.AppendLine(C("36", "[Changed]") + $" {TextSafety.EscapeName(m.FullName)}  {TextSafety.EscapeName(m.Signature)}" + (stats.Count > 0 ? "  (" + string.Join(", ", stats) + ")" : ""));
                foreach (var c in m.CallsAdded) sb.AppendLine(C("32", $"  + call {TextSafety.EscapeName(c)}"));
                foreach (var c in m.CallsRemoved) sb.AppendLine(C("31", $"  - call {TextSafety.EscapeName(c)}"));
                Patch(sb, C, m.ILDiff);
                if (!string.IsNullOrEmpty(m.PseudoDiff))
                {
                    sb.AppendLine("  pseudocode:");
                    Patch(sb, C, m.PseudoDiff);
                }
            }
            if (a.Metadata.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(C("36", "[Metadata]"));
                foreach (var c in a.Metadata)
                    sb.AppendLine($"  {c.Kind} {c.Change}: {TextSafety.EscapeName(c.Item)}" + (c.Detail != null ? $"  ({TextSafety.EscapeName(c.Detail)})" : ""));
            }
            if (a.NewFindings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(C("36", "[Audit]") + "  new findings");
                foreach (var f in a.NewFindings)
                {
                    var at = f.Offset is int off ? $"IL_{off:X4}" : "decl.";
                    sb.AppendLine($"  {f.RuleId}  {f.Level.ToString().ToLowerInvariant(),-8} {TextSafety.EscapeName(f.Method.FullName)} {at}  {TextSafety.EscapeName(f.Message)}");
                }
            }
        }
        return sb.ToString();
    }

    private static void List(StringBuilder sb, Func<string, string, string> c, string title, IEnumerable<MethodDiff> methods)
    {
        var list = methods.ToList();
        if (list.Count == 0) return;
        sb.AppendLine();
        sb.AppendLine(c("36", title));
        foreach (var m in list) sb.AppendLine($"  {TextSafety.EscapeName(m.FullName)}  {TextSafety.EscapeName(m.Signature)}");
    }

    private static void Patch(StringBuilder sb, Func<string, string, string> c, string? diff)
    {
        if (string.IsNullOrEmpty(diff)) return;
        foreach (var line in diff.TrimEnd('\n').Split('\n'))
        {
            var text = "  " + TextSafety.EscapeName(line);
            sb.AppendLine(line.Length == 0 ? text : line[0] switch
            {
                '+' when !line.StartsWith("+++", StringComparison.Ordinal) => c("32", text),
                '-' when !line.StartsWith("---", StringComparison.Ordinal) => c("31", text),
                '@' => c("36", text),
                _ => text,
            });
        }
    }

    public static string Json(DiffResult result)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("tool", "NetScope");
            w.WriteString("version", Program.Version);
            w.WriteBoolean("changed", result.HasDifferences);
            Strings(w, "assembliesRemoved", result.OnlyOld);
            Strings(w, "assembliesAdded", result.OnlyNew);
            w.WriteStartArray("assemblies");
            foreach (var a in result.Assemblies) Assembly(w, a);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()) + Environment.NewLine;
    }

    private static void Assembly(Utf8JsonWriter w, AssemblyDiff a)
    {
        w.WriteStartObject();
        w.WriteString("assembly", a.Name);
        w.WriteString("old", a.OldPath);
        w.WriteString("new", a.NewPath);
        w.WriteBoolean("changed", a.HasDifferences);
        w.WriteStartObject("summary");
        foreach (var s in Enum.GetValues<MethodStatus>()) w.WriteNumber(s.ToString().ToLowerInvariant(), a.Count(s));
        w.WriteEndObject();
        w.WriteStartArray("methods");
        foreach (var m in a.Methods.Where(m => m.Status != MethodStatus.Unchanged))
        {
            w.WriteStartObject();
            w.WriteString("status", m.StatusText);
            w.WriteString("method", m.FullName);
            w.WriteString("signature", m.Signature);
            w.WriteString("key", m.Key);
            if (m.Status == MethodStatus.Changed)
            {
                if (m.OldComplexity != null || m.NewComplexity != null)
                {
                    w.WriteStartObject("complexity");
                    if (m.OldComplexity is int oc) w.WriteNumber("old", oc);
                    if (m.NewComplexity is int nc) w.WriteNumber("new", nc);
                    w.WriteEndObject();
                }
                if (m.OldBlocks != null || m.NewBlocks != null)
                {
                    w.WriteStartObject("blocks");
                    if (m.OldBlocks is int ob) w.WriteNumber("old", ob);
                    if (m.NewBlocks is int nb) w.WriteNumber("new", nb);
                    w.WriteEndObject();
                }
                Strings(w, "callsAdded", m.CallsAdded);
                Strings(w, "callsRemoved", m.CallsRemoved);
                if (m.ILDiff != null) w.WriteString("ilDiff", m.ILDiff);
                if (m.PseudoDiff != null) w.WriteString("pseudocodeDiff", m.PseudoDiff);
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("metadata");
        foreach (var c in a.Metadata)
        {
            w.WriteStartObject();
            w.WriteString("kind", c.Kind);
            w.WriteString("change", c.Change);
            w.WriteString("item", c.Item);
            if (c.Detail != null) w.WriteString("detail", c.Detail);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("newFindings");
        foreach (var f in a.NewFindings)
        {
            w.WriteStartObject();
            w.WriteString("rule", f.RuleId);
            w.WriteString("level", f.Level.ToString().ToLowerInvariant());
            w.WriteString("method", f.Method.FullName);
            w.WriteString("message", f.Message);
            if (f.Offset is int off) w.WriteNumber("offset", off);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        Strings(w, "errors", a.Errors);
        w.WriteEndObject();
    }

    private static void Strings(Utf8JsonWriter w, string name, IEnumerable<string> values)
    {
        w.WriteStartArray(name);
        foreach (var v in values) w.WriteStringValue(v);
        w.WriteEndArray();
    }
}
