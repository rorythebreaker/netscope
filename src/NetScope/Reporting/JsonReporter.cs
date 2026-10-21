using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using NetScope.Calls;
using NetScope.Cfg;

namespace NetScope.Reporting;

/// <summary>
/// Structured output with stable field names. Absent groups are not written;
/// per-method errors go to "errors".
/// </summary>
public sealed class JsonReporter : IReporter
{
    private static readonly JsonWriterOptions Options = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly OutputGroups _groups;
    private bool _arrayClosed;

    public JsonReporter(OutputGroups groups) => _groups = groups;

    public string Extension => "json";

    public string BeginDocument()
    {
        var nl = Environment.NewLine;
        return "{" + nl +
               $"  \"tool\": \"NetScope\",{nl}" +
               $"  \"version\": \"{Program.Version}\",{nl}" +
               "  \"assemblies\": [";
    }

    public string RenderAssembly(AssemblyReport report, bool first)
    {
        var json = Write(w => WriteAssembly(w, report));
        return (first ? "" : ",") + Environment.NewLine + Indent(json, 4);
    }

    public string RenderCallGraph(IReadOnlyList<CallGraphEdge> edges, bool afterAssemblies)
    {
        _arrayClosed = true;
        var json = Write(w => WriteCallGraph(w, edges));
        return Environment.NewLine + "  ]," + Environment.NewLine + "  \"callgraph\": " + Indent(json, 2).TrimStart();
    }

    public string EndDocument() => (_arrayClosed ? "" : Environment.NewLine + "  ]") + Environment.NewLine + "}" + Environment.NewLine;

    private static string Write(Action<Utf8JsonWriter> body)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Options)) body(w);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string Indent(string text, int spaces)
    {
        var pad = new string(' ', spaces);
        return string.Join(Environment.NewLine, TextReporter.SplitLines(text).Select(l => pad + l));
    }

    private static void WriteAssembly(Utf8JsonWriter w, AssemblyReport report)
    {
        var asm = report.Assembly;
        w.WriteStartObject();
        w.WriteString("file", asm.FileName);
        w.WriteString("path", asm.FilePath);
        w.WriteString("assembly", asm.Name);
        if (asm.Version != null) w.WriteString("version", asm.Version.ToString());
        if (asm.TargetFramework != null) w.WriteString("targetFramework", asm.TargetFramework);
        w.WriteString("metadataVersion", asm.MetadataVersion);
        w.WriteString("machine", asm.Architecture);
        w.WriteBoolean("readyToRun", asm.IsReadyToRun);
        w.WriteBoolean("mixedMode", asm.IsMixedMode);
        WriteStrings(w, "notes", asm.Notes);
        WriteStrings(w, "errors", report.Errors);
        w.WriteStartArray("methods");
        foreach (var m in report.Methods) WriteMethod(w, m);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteMethod(Utf8JsonWriter w, MethodReport r)
    {
        var m = r.Method;
        w.WriteStartObject();
        w.WriteString("assembly", m.Assembly.Name);
        w.WriteString("namespace", m.Namespace);
        w.WriteString("type", m.TypeName);
        w.WriteString("name", m.Name);
        w.WriteString("signature", m.Signature);
        w.WriteNumber("token", m.Token);
        w.WriteString("access", m.Access);
        WriteStrings(w, "kinds", m.Kinds);
        w.WriteNumber("rva", m.Rva);
        if (r.CodeSize is int size) w.WriteNumber("size", size);
        if (m.PInvoke != null)
        {
            w.WriteStartObject("pinvoke");
            w.WriteString("dll", m.PInvoke.Dll);
            w.WriteString("entryPoint", m.PInvoke.EntryPoint);
            w.WriteEndObject();
        }

        if (r.Calls != null)
        {
            w.WriteStartArray("calls");
            foreach (var c in r.Calls)
            {
                w.WriteStartObject();
                w.WriteNumber("offset", c.Site.Offset);
                w.WriteString("opcode", c.Site.Kind);
                w.WriteString("target", c.Site.Display);
                w.WriteString("scope", c.Site.Scope);
                if (c.Site.Target?.Assembly != null) w.WriteString("targetAssembly", c.Site.Target.Assembly);
                if (c.GroupTarget != null)
                {
                    w.WriteString("groupFile", c.GroupTarget.Assembly.FileName);
                    w.WriteNumber("groupToken", c.GroupTarget.Token);
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        if (r.Callers != null)
        {
            w.WriteStartArray("callers");
            foreach (var c in r.Callers)
            {
                w.WriteStartObject();
                w.WriteString("assembly", c.Caller.Assembly.Name);
                w.WriteString("method", c.Caller.FullName);
                w.WriteString("signature", c.Caller.Signature);
                w.WriteNumber("offset", c.Offset);
                w.WriteString("opcode", c.Kind);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        if (r.Cfg != null) WriteCfg(w, r.Cfg);
        if (r.Pseudo != null)
        {
            w.WriteString("pseudocode", r.Pseudo.Text.Replace("\r\n", "\n"));
            w.WriteString("pseudocodeSource", r.Pseudo.Source);
            if (r.Pseudo.Reason != null) w.WriteString("pseudocodeError", r.Pseudo.Reason);
        }
        if (r.IL != null)
        {
            w.WriteStartArray("il");
            foreach (var l in r.IL.Lines)
            {
                w.WriteStartObject();
                w.WriteNumber("offset", l.Offset);
                w.WriteString("opcode", l.OpCode);
                if (l.Operand.Length > 0) w.WriteString("operand", l.Operand);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartObject("ilInfo");
            w.WriteNumber("maxStack", r.IL.MaxStack);
            w.WriteBoolean("localsInit", r.IL.LocalsInit);
            WriteStrings(w, "locals", r.IL.Locals);
            WriteStrings(w, "exceptionClauses", r.IL.ExceptionClauses);
            w.WriteEndObject();
        }
        if (r.Native != null)
        {
            var n = r.Native;
            w.WriteStartArray("native");
            foreach (var l in n.Lines)
            {
                w.WriteStartObject();
                if (n.Source != "JIT")
                {
                    w.WriteNumber("address", l.Address);
                    w.WriteString("bytes", l.Bytes);
                }
                if (n.Parts.Count > 1 && l.Part != null) w.WriteString("part", l.Part);
                w.WriteString("text", l.Text);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartObject("nativeInfo");
            w.WriteBoolean("available", n.Available);
            if (n.Reason != null) w.WriteString("reason", n.Reason);
            if (n.Source != null) w.WriteString("source", n.Source);
            if (n.Architecture != null) w.WriteString("architecture", n.Architecture);
            if (n.RuntimeVersion != null) w.WriteString("runtime", n.RuntimeVersion);
            if (n.Tier != null) w.WriteString("tier", n.Tier);
            if (n.Available) w.WriteNumber("size", n.Size);
            if (n.Parts.Count > 0)
            {
                w.WriteStartArray("parts");
                foreach (var p in n.Parts)
                {
                    w.WriteStartObject();
                    w.WriteString("name", p.Name);
                    w.WriteNumber("address", p.Address);
                    w.WriteNumber("size", p.Size);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        w.WriteStartArray("errors");
        foreach (var e in r.Errors) w.WriteStringValue(e);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteCfg(Utf8JsonWriter w, ControlFlowGraph cfg)
    {
        w.WriteNumber("complexity", cfg.CyclomaticComplexity);
        w.WriteStartArray("blocks");
        foreach (var b in cfg.Blocks)
        {
            w.WriteStartObject();
            w.WriteString("id", b.Name);
            w.WriteNumber("start", b.StartOffset);
            w.WriteNumber("end", b.EndOffset);
            w.WriteBoolean("reachable", b.Reachable);
            w.WriteBoolean("loopHeader", b.IsLoopHeader);
            if (b.ExitKind != null) w.WriteString("exit", b.ExitKind);
            if (b.Region != null) w.WriteNumber("region", b.Region.Index);
            w.WriteStartArray("instructions");
            foreach (var i in b.Instructions) w.WriteNumberValue(i.Offset);
            w.WriteEndArray();
            w.WriteStartArray("successors");
            foreach (var e in b.Successors)
            {
                w.WriteStartObject();
                w.WriteString("target", e.To.Name);
                w.WriteString("kind", e.KindName);
                if (e.Kind == EdgeKind.Case) w.WriteNumber("case", e.CaseIndex);
                if (e.Condition != null) w.WriteString("condition", e.Condition);
                if (e.IsBackEdge) w.WriteBoolean("backEdge", true);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("loops");
        foreach (var l in cfg.Loops)
        {
            w.WriteStartObject();
            w.WriteString("header", l.Header.Name);
            WriteStrings(w, "latches", l.Latches.Select(b => b.Name));
            WriteStrings(w, "body", l.Body.Select(b => b.Name));
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("regions");
        foreach (var r in cfg.AllRegions())
        {
            w.WriteStartObject();
            w.WriteNumber("id", r.Index);
            w.WriteString("kind", r.Kind);
            w.WriteNumber("start", r.Start);
            w.WriteNumber("end", r.End);
            if (r.CatchType != null) w.WriteString("catchType", r.CatchType);
            if (r.Try != null) w.WriteNumber("try", r.Try.Index);
            if (r.Parent != null) w.WriteNumber("parent", r.Parent.Index);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        WriteStrings(w, "unreachable", cfg.Unreachable.Select(b => b.Name));
        WriteStrings(w, "exits", cfg.Exits.Select(b => b.Name));
    }

    private static void WriteCallGraph(Utf8JsonWriter w, IReadOnlyList<CallGraphEdge> edges)
    {
        var ids = CallGraphNodes.Assign(edges);
        w.WriteStartObject();
        w.WriteStartArray("nodes");
        foreach (var (m, id) in ids)
        {
            w.WriteStartObject();
            w.WriteString("id", id);
            w.WriteString("assembly", m.Assembly.Name);
            w.WriteString("method", m.FullName);
            w.WriteString("signature", m.Signature);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("edges");
        foreach (var e in edges)
        {
            w.WriteStartObject();
            w.WriteString("from", ids[e.Caller]);
            w.WriteString("to", ids[e.Callee]);
            WriteStrings(w, "opcodes", e.Sites.Select(s => s.Kind).Distinct());
            w.WriteStartArray("offsets");
            foreach (var s in e.Sites) w.WriteNumberValue(s.Offset);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteStrings(Utf8JsonWriter w, string name, IEnumerable<string> values)
    {
        w.WriteStartArray(name);
        foreach (var v in values) w.WriteStringValue(v);
        w.WriteEndArray();
    }
}

/// <summary>Stable node ids for call graphs, in order of first appearance.</summary>
public static class CallGraphNodes
{
    public static Dictionary<Catalog.MethodModel, string> Assign(IEnumerable<CallGraphEdge> edges)
    {
        var ids = new Dictionary<Catalog.MethodModel, string>();
        foreach (var e in edges)
        {
            foreach (var m in new[] { e.Caller, e.Callee })
                if (!ids.ContainsKey(m)) ids[m] = "n" + ids.Count;
        }
        return ids;
    }
}
