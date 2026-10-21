using System.Text;

namespace NetScope.Ghidra;

/// <summary>
/// Renders <see cref="GhidraAnnotations"/> as a Ghidra annotation script (TZ-NetScope-2 §5.3–5.4). The script
/// only renames functions, writes comments and sets bookmarks through the Ghidra API; it never touches the
/// network, reads other files or runs external commands. Re-running it updates the labels instead of
/// duplicating them (idempotent). Names and comments are escaped for the target language, Unicode included.
/// </summary>
public static class GhidraScriptWriter
{
    /// <summary>The Ghidra version this output was designed and checked against.</summary>
    public const string GhidraVersion = "11.1.2";

    public static string Write(GhidraAnnotations ann, string lang) =>
        lang == "java" ? WriteJava(ann) : WritePython(ann);

    private static string[] HeaderLines(GhidraAnnotations ann) => new[]
    {
        "NetScope -> Ghidra annotation script.",
        "",
        "Source assembly : " + ann.AssemblyName,
        "Architecture    : " + (ann.Architecture ?? "unknown"),
        "Ghidra version  : " + GhidraVersion,
        "",
        "This script ONLY does the following, and nothing else:",
        $"  - name {ann.Functions.Count} function(s) at their image-base-relative address",
        "  - set a plate comment with the .NET signature on each",
        $"  - label {ann.Funclets.Count} funclet(s) and link them to their parent method",
        $"  - label {ann.Imports.Count} IAT import thunk(s)",
        $"  - set {ann.Bookmarks.Count} bookmark(s) at points of interest (--audit)",
        "",
        "It does NOT access the network, read other files, or run external commands.",
        "Addresses are offsets from the image base; Ghidra maps them onto its own base.",
        "Read it before running:  analyzeHeadless <proj> <name> -process <file> -postScript " + "<this script>",
    };

    // ------------------------------------------------------------------ Python (Ghidrathon / Jython)

    private static string WritePython(GhidraAnnotations ann)
    {
        var sb = new StringBuilder();
        foreach (var line in HeaderLines(ann)) sb.Append("# ").AppendLine(line);
        sb.AppendLine();
        sb.AppendLine("from ghidra.program.flatapi import FlatProgramAPI");
        sb.AppendLine("from ghidra.util.task import ConsoleTaskMonitor");
        sb.AppendLine("from ghidra.program.model.symbol import SourceType");
        sb.AppendLine();
        sb.AppendLine("_api = FlatProgramAPI(currentProgram, ConsoleTaskMonitor())");
        sb.AppendLine("_base = currentProgram.getImageBase()");
        sb.AppendLine("_bm = currentProgram.getBookmarkManager()");
        sb.AppendLine();
        sb.AppendLine("def _ea(rva):");
        sb.AppendLine("    return _base.add(rva)");
        sb.AppendLine();
        sb.AppendLine("def _name_function(rva, name, signature):");
        sb.AppendLine("    addr = _ea(rva)");
        sb.AppendLine("    f = _api.getFunctionAt(addr)");
        sb.AppendLine("    if f is None:");
        sb.AppendLine("        f = _api.createFunction(addr, name)");
        sb.AppendLine("    if f is not None:");
        sb.AppendLine("        f.setName(name, SourceType.USER_DEFINED)   # idempotent: updates in place");
        sb.AppendLine("        if signature:");
        sb.AppendLine("            _api.setPlateComment(addr, signature)");
        sb.AppendLine("    else:");
        sb.AppendLine("        _api.createLabel(addr, name, True)");
        sb.AppendLine();
        sb.AppendLine("def _label(rva, name, comment):");
        sb.AppendLine("    addr = _ea(rva)");
        sb.AppendLine("    _api.createLabel(addr, name, True)   # idempotent: same name is reused");
        sb.AppendLine("    if comment:");
        sb.AppendLine("        _api.setEOLComment(addr, comment)");
        sb.AppendLine();
        sb.AppendLine("def _bookmark(rva, category, note):");
        sb.AppendLine("    addr = _ea(rva)");
        sb.AppendLine("    for b in _bm.getBookmarks(addr):");
        sb.AppendLine("        if b.getType().getTypeString() == 'Note' and b.getCategory() == category:");
        sb.AppendLine("            _bm.removeBookmark(b)   # idempotent: drop a prior run's bookmark first");
        sb.AppendLine("    _api.createBookmark(addr, category, note)");
        sb.AppendLine();
        foreach (var f in ann.Functions)
            sb.AppendLine($"_name_function({f.Rva}, {Py(f.Name)}, {Py(f.Signature)})");
        foreach (var f in ann.Funclets)
            sb.AppendLine($"_label({f.Rva}, {Py(FuncletName(f))}, {Py("funclet of " + f.ParentName)})");
        foreach (var i in ann.Imports)
            sb.AppendLine($"_label({i.Rva}, {Py("imp_" + Sanitize(i.Name))}, {Py(i.Name)})");
        foreach (var b in ann.Bookmarks)
            sb.AppendLine($"_bookmark({b.Rva}, {Py(b.Category)}, {Py(b.Note)})");
        sb.AppendLine();
        sb.AppendLine("print('NetScope: annotations applied')");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ Java (GhidraScript)

    private static string WriteJava(GhidraAnnotations ann)
    {
        var sb = new StringBuilder();
        foreach (var line in HeaderLines(ann)) sb.Append("// ").AppendLine(line);
        sb.AppendLine();
        sb.AppendLine("import ghidra.app.script.GhidraScript;");
        sb.AppendLine("import ghidra.program.model.address.Address;");
        sb.AppendLine("import ghidra.program.model.listing.Function;");
        sb.AppendLine("import ghidra.program.model.symbol.SourceType;");
        sb.AppendLine("import ghidra.program.model.listing.BookmarkManager;");
        sb.AppendLine("import ghidra.program.model.listing.Bookmark;");
        sb.AppendLine();
        sb.AppendLine("public class NetScopeAnnotate extends GhidraScript {");
        sb.AppendLine("    @Override");
        sb.AppendLine("    public void run() throws Exception {");
        sb.AppendLine("        Address base = currentProgram.getImageBase();");
        sb.AppendLine("        BookmarkManager bm = currentProgram.getBookmarkManager();");
        foreach (var f in ann.Functions)
            sb.AppendLine($"        nameFunction(base, {f.Rva}, {Jv(f.Name)}, {Jv(f.Signature)});");
        foreach (var f in ann.Funclets)
            sb.AppendLine($"        label(base, {f.Rva}, {Jv(FuncletName(f))}, {Jv("funclet of " + f.ParentName)});");
        foreach (var i in ann.Imports)
            sb.AppendLine($"        label(base, {i.Rva}, {Jv("imp_" + Sanitize(i.Name))}, {Jv(i.Name)});");
        foreach (var b in ann.Bookmarks)
            sb.AppendLine($"        bookmark(bm, base, {b.Rva}, {Jv(b.Category)}, {Jv(b.Note)});");
        sb.AppendLine("        println(\"NetScope: annotations applied\");");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private void nameFunction(Address base, int rva, String name, String signature) throws Exception {");
        sb.AppendLine("        Address addr = base.add(rva);");
        sb.AppendLine("        Function f = getFunctionAt(addr);");
        sb.AppendLine("        if (f == null) f = createFunction(addr, name);");
        sb.AppendLine("        if (f != null) {");
        sb.AppendLine("            f.setName(name, SourceType.USER_DEFINED);   // idempotent: updates in place");
        sb.AppendLine("            if (signature != null && !signature.isEmpty()) setPlateComment(addr, signature);");
        sb.AppendLine("        } else {");
        sb.AppendLine("            createLabel(addr, name, true);");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private void label(Address base, int rva, String name, String comment) throws Exception {");
        sb.AppendLine("        Address addr = base.add(rva);");
        sb.AppendLine("        createLabel(addr, name, true);   // idempotent: same name is reused");
        sb.AppendLine("        if (comment != null && !comment.isEmpty()) setEOLComment(addr, comment);");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private void bookmark(BookmarkManager bm, Address base, int rva, String category, String note) {");
        sb.AppendLine("        Address addr = base.add(rva);");
        sb.AppendLine("        for (Bookmark b : bm.getBookmarks(addr)) {");
        sb.AppendLine("            if (b.getType().getTypeString().equals(\"Note\") && b.getCategory().equals(category))");
        sb.AppendLine("                bm.removeBookmark(b);   // idempotent: drop a prior run's bookmark first");
        sb.AppendLine("        }");
        sb.AppendLine("        createBookmark(addr, category, note);");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string FuncletName(GhidraFunclet f) => Sanitize(f.ParentName) + "__" + Sanitize(f.Label);

    /// <summary>A symbol-safe identifier: anything but a letter, digit or underscore becomes '_'.</summary>
    private static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        return sb.Length == 0 ? "_" : sb.ToString();
    }

    /// <summary>Python string literal (None for null). Non-ASCII becomes \uXXXX so the file stays ASCII.</summary>
    private static string Py(string? s) => s == null ? "None" : "\"" + Escape(s) + "\"";

    /// <summary>Java string literal (null for null).</summary>
    private static string Jv(string? s) => s == null ? "null" : "\"" + Escape(s) + "\"";

    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20 || c > 0x7e) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
