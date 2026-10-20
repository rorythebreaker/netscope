using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using NetScope.Audit;
using NetScope.Calls;

namespace NetScope.Reporting;

/// <summary>A reporter whose output is one document built from the fragments of all assemblies, in input order.</summary>
public interface IDocumentReporter
{
    string Document(IReadOnlyList<string> assemblyFragments);
}

/// <summary>
/// SARIF 2.1.0 for --audit: one run, one rule per ID, one result per finding with the assembly file,
/// the full method name, the IL offset and, with a PDB, the source line. Loadable by GitHub Code Scanning.
/// </summary>
public sealed class SarifReporter : IReporter, IDocumentReporter
{
    public const string SchemaUri = "https://json.schemastore.org/sarif-2.1.0.json";
    private const string InformationUri = "https://github.com/rorythebreaker/netscope";

    private static readonly JsonWriterOptions Options = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly IReadOnlyList<IRule> _rules;
    private readonly string _baseDirectory;

    public SarifReporter(IReadOnlyList<IRule> rules, string? baseDirectory = null)
    {
        _rules = rules;
        _baseDirectory = Path.GetFullPath(baseDirectory ?? Directory.GetCurrentDirectory());
    }

    public string Extension => "sarif";
    public string BeginDocument() => "";
    public string EndDocument() => "";
    public string RenderCallGraph(IReadOnlyList<CallGraphEdge> edges, bool afterAssemblies) => "";

    /// <summary>A JSON array with the results of one assembly.</summary>
    public string RenderAssembly(AssemblyReport report, bool first) => Write(w =>
    {
        w.WriteStartArray();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var m in report.Methods)
            foreach (var f in m.Findings ?? Array.Empty<Finding>())
            {
                // Identical findings in one method (two equal calls) are told apart by their order.
                var key = f.RuleId + "|" + f.Method.FullName + "|" + f.Method.Signature + "|" + f.Message;
                int occurrence = seen.GetValueOrDefault(key);
                seen[key] = occurrence + 1;
                WriteResult(w, f, report, occurrence);
            }
        w.WriteEndArray();
    });

    public string Document(IReadOnlyList<string> assemblyFragments) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteString("$schema", SchemaUri);
        w.WriteString("version", "2.1.0");
        w.WriteStartArray("runs");
        w.WriteStartObject();
        w.WriteStartObject("tool");
        w.WriteStartObject("driver");
        w.WriteString("name", "NetScope");
        w.WriteString("version", Program.Version);
        w.WriteString("semanticVersion", Program.Version);
        w.WriteString("informationUri", InformationUri);
        w.WriteStartArray("rules");
        foreach (var r in _rules) WriteRule(w, r);
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndObject();
        w.WriteString("columnKind", "utf16CodeUnits");
        w.WriteStartArray("results");
        foreach (var fragment in assemblyFragments)
        {
            if (string.IsNullOrWhiteSpace(fragment)) continue;
            using var doc = JsonDocument.Parse(fragment);
            foreach (var result in doc.RootElement.EnumerateArray()) result.WriteTo(w);
        }
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndArray();
        w.WriteEndObject();
    }) + Environment.NewLine;

    private static void WriteRule(Utf8JsonWriter w, IRule r)
    {
        w.WriteStartObject();
        w.WriteString("id", r.Id);
        w.WriteString("name", r.Name);
        w.WriteStartObject("shortDescription");
        w.WriteString("text", r.Title);
        w.WriteEndObject();
        w.WriteStartObject("fullDescription");
        w.WriteString("text", r.Description);
        w.WriteEndObject();
        w.WriteString("helpUri", InformationUri + "#security-rules");
        w.WriteStartObject("defaultConfiguration");
        w.WriteString("level", Level(r.Level));
        w.WriteEndObject();
        w.WriteStartObject("properties");
        w.WriteStartArray("tags");
        w.WriteStringValue("security");
        w.WriteEndArray();
        // GitHub Code Scanning maps this to critical/high/medium/low.
        w.WriteString("security-severity", r.Level switch { Severity.Error => "8.0", Severity.Warning => "5.0", _ => "2.0" });
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private void WriteResult(Utf8JsonWriter w, Finding f, AssemblyReport report, int occurrence)
    {
        int ruleIndex = -1;
        for (int i = 0; i < _rules.Count; i++)
            if (_rules[i].Id == f.RuleId) ruleIndex = i;
        var method = f.Method;
        var assemblyUri = Uri(report.Assembly.FilePath);

        w.WriteStartObject();
        w.WriteString("ruleId", f.RuleId);
        if (ruleIndex >= 0) w.WriteNumber("ruleIndex", ruleIndex);
        w.WriteString("level", Level(f.Level));
        w.WriteStartObject("message");
        w.WriteString("text", $"{method.FullName}: {f.Message}");
        w.WriteEndObject();

        w.WriteStartArray("locations");
        w.WriteStartObject();
        w.WriteStartObject("physicalLocation");
        if (f.Source != null)
        {
            WriteArtifact(w, Uri(f.Source.Document));
            w.WriteStartObject("region");
            w.WriteNumber("startLine", f.Source.StartLine);
            if (f.Source.StartColumn > 0) w.WriteNumber("startColumn", f.Source.StartColumn);
            if (f.Source.EndLine >= f.Source.StartLine) w.WriteNumber("endLine", f.Source.EndLine);
            if (f.Source.EndColumn > 0 && f.Source.EndLine >= f.Source.StartLine) w.WriteNumber("endColumn", f.Source.EndColumn);
            w.WriteEndObject();
        }
        else
        {
            WriteArtifact(w, assemblyUri);
        }
        w.WriteEndObject();
        w.WriteStartArray("logicalLocations");
        w.WriteStartObject();
        w.WriteString("name", method.Name);
        w.WriteString("fullyQualifiedName", method.FullName);
        w.WriteString("kind", "function");
        w.WriteEndObject();
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndArray();

        if (f.Source != null)
        {
            // The assembly itself, so the binary location survives when the source is shown.
            w.WriteStartArray("relatedLocations");
            w.WriteStartObject();
            w.WriteNumber("id", 1);
            w.WriteStartObject("physicalLocation");
            WriteArtifact(w, assemblyUri);
            w.WriteEndObject();
            w.WriteStartObject("message");
            w.WriteString("text", "assembly " + report.Assembly.FileName);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndArray();
        }

        w.WriteStartObject("partialFingerprints");
        w.WriteString("netscope/v1", Fingerprint(f, report, occurrence));
        w.WriteEndObject();

        w.WriteStartObject("properties");
        w.WriteString("assembly", report.Assembly.FileName);
        w.WriteString("method", method.FullName);
        w.WriteString("signature", method.Signature);
        if (f.Offset is int offset)
        {
            w.WriteNumber("ilOffset", offset);
            w.WriteString("ilLabel", $"IL_{offset:X4}");
        }
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void WriteArtifact(Utf8JsonWriter w, string uri)
    {
        w.WriteStartObject("artifactLocation");
        w.WriteString("uri", uri);
        w.WriteEndObject();
    }

    /// <summary>Relative URI (forward slashes) for files under the base directory, an absolute file URI otherwise.</summary>
    internal string Uri(string path)
    {
        if (!System.IO.Path.IsPathFullyQualified(path)) return path.Replace('\\', '/');
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(_baseDirectory, full);
        if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            return string.Join('/', relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Select(System.Uri.EscapeDataString));
        return new System.Uri(full).AbsoluteUri;
    }

    /// <summary>Stable across builds: rule, assembly name, method, message and occurrence; not the IL offset.</summary>
    private static string Fingerprint(Finding f, AssemblyReport report, int occurrence)
    {
        var text = string.Join("|", f.RuleId, report.Assembly.Name, f.Method.FullName, f.Method.Signature, f.Message, occurrence);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 16).ToLowerInvariant();
    }

    private static string Level(Severity s) => s switch { Severity.Error => "error", Severity.Warning => "warning", _ => "note" };

    private static string Write(Action<Utf8JsonWriter> body)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Options)) body(w);
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
