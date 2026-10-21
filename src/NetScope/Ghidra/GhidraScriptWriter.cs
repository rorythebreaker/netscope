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
    /// <summary>The Ghidra version this output was checked against (analyzeHeadless; Jython, Ghidrathon and GhidraScript).</summary>
    public const string GhidraVersion = "11.1.2";

    /// <summary>The Ghidrathon version the Python script was checked against (Python 3 through Jep).</summary>
    public const string GhidrathonVersion = "4.0.0";

    /// <summary>Class name used when the caller does not derive one from the output file name.</summary>
    public const string DefaultJavaClass = "NetScopeAnnotate";

    /// <param name="javaClassName">GhidraScript requires the public class to match the .java file name.</param>
    public static string Write(GhidraAnnotations ann, string lang, string javaClassName = DefaultJavaClass) =>
        lang == "java" ? WriteJava(ann, javaClassName) : WritePython(ann);

    private static readonly HashSet<string> JavaKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "assert", "boolean", "break", "byte", "case", "catch", "char", "class", "const", "continue",
        "default", "do", "double", "else", "enum", "extends", "final", "finally", "float", "for", "goto", "if",
        "implements", "import", "instanceof", "int", "interface", "long", "native", "new", "package", "private",
        "protected", "public", "return", "short", "static", "strictfp", "super", "switch", "synchronized", "this",
        "throw", "throws", "transient", "try", "void", "volatile", "while", "true", "false", "null", "var", "record",
    };

    /// <summary>Whether a .java file name (without extension) can be the name of its public class.</summary>
    public static bool IsValidJavaClassName(string name) =>
        name.Length > 0 && (char.IsLetter(name[0]) || name[0] is '_' or '$') &&
        name.All(c => char.IsLetterOrDigit(c) || c is '_' or '$') && !JavaKeywords.Contains(name);

    /// <summary>
    /// Ghidra rejects symbol names that contain whitespace or control characters
    /// (e.g. "SelectEnumerableIterator&lt;char, char&gt;::ToArray"); such characters become '_'.
    /// </summary>
    public static string SymbolName(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(char.IsWhiteSpace(c) || char.IsControl(c) ? '_' : c);
        return sb.Length == 0 ? "_" : sb.ToString();
    }

    private static string[] HeaderLines(GhidraAnnotations ann) => new[]
    {
        "NetScope -> Ghidra annotation script.",
        "",
        "Source assembly : " + ann.AssemblyName,
        "Architecture    : " + (ann.Architecture ?? "unknown"),
        "Ghidra version  : " + GhidraVersion + " (Python: Jython 2.7, or Ghidrathon " + GhidrathonVersion + " with Python 3)",
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
        sb.AppendLine("import sys");
        sb.AppendLine("from ghidra.program.flatapi import FlatProgramAPI");
        sb.AppendLine("from ghidra.util.task import ConsoleTaskMonitor");
        sb.AppendLine("from ghidra.program.model.symbol import SourceType");
        sb.AppendLine();
        sb.AppendLine("# Jython exposes the script state as variables, Ghidrathon (Python 3) as functions: currentProgram().");
        sb.AppendLine("_program = currentProgram() if callable(currentProgram) else currentProgram");
        sb.AppendLine("_api = FlatProgramAPI(_program, ConsoleTaskMonitor())");
        sb.AppendLine("_base = _program.getImageBase()");
        sb.AppendLine("_bm = _program.getBookmarkManager()");
        sb.AppendLine("_failed = []");
        sb.AppendLine();
        sb.AppendLine("def _ea(rva):");
        sb.AppendLine("    return _base.add(rva)");
        sb.AppendLine();
        sb.AppendLine("def _name_function(rva, name, signature):");
        sb.AppendLine("    try:");
        sb.AppendLine("        addr = _ea(rva)");
        sb.AppendLine("        f = _api.getFunctionAt(addr)");
        sb.AppendLine("        if f is None:");
        sb.AppendLine("            f = _api.createFunction(addr, name)");
        sb.AppendLine("        if f is not None:");
        sb.AppendLine("            f.setName(name, SourceType.USER_DEFINED)   # idempotent: updates in place");
        sb.AppendLine("            if signature:");
        sb.AppendLine("                _api.setPlateComment(addr, signature)");
        sb.AppendLine("        else:");
        sb.AppendLine("            _api.createLabel(addr, name, True)");
        sb.AppendLine("    except:   # one bad entry must not stop the rest (Java and Python exceptions alike)");
        sb.AppendLine("        _failed.append(rva)");
        sb.AppendLine();
        sb.AppendLine("def _label(rva, name, comment):");
        sb.AppendLine("    try:");
        sb.AppendLine("        addr = _ea(rva)");
        sb.AppendLine("        _api.createLabel(addr, name, True)   # idempotent: same name is reused");
        sb.AppendLine("        if comment:");
        sb.AppendLine("            _api.setEOLComment(addr, comment)");
        sb.AppendLine("    except:");
        sb.AppendLine("        _failed.append(rva)");
        sb.AppendLine();
        sb.AppendLine("def _bookmark(rva, category, note):");
        sb.AppendLine("    try:");
        sb.AppendLine("        addr = _ea(rva)");
        sb.AppendLine("        for b in _bm.getBookmarks(addr):");
        sb.AppendLine("            if b.getType().getTypeString() == 'Note' and b.getCategory() == category:");
        sb.AppendLine("                _bm.removeBookmark(b)   # idempotent: drop a prior run's bookmark first");
        sb.AppendLine("        _api.createBookmark(addr, category, note)");
        sb.AppendLine("    except:");
        sb.AppendLine("        _failed.append(rva)");
        sb.AppendLine();
        foreach (var f in ann.Functions)
            sb.AppendLine($"_name_function({f.Rva}, {Py(SymbolName(f.Name))}, {Py(f.Signature)})");
        foreach (var f in ann.Funclets)
            sb.AppendLine($"_label({f.Rva}, {Py(FuncletName(f))}, {Py("funclet of " + f.ParentName)})");
        foreach (var i in ann.Imports)
            sb.AppendLine($"_label({i.Rva}, {Py("imp_" + Sanitize(i.Name))}, {Py(i.Name)})");
        foreach (var b in ann.Bookmarks)
            sb.AppendLine($"_bookmark({b.Rva}, {Py(b.Category)}, {Py(b.Note)})");
        sb.AppendLine();
        sb.AppendLine("_python = 'Python %d.%d' % (sys.version_info[0], sys.version_info[1])");
        sb.AppendLine("if _failed:");
        sb.AppendLine("    print('NetScope: annotations applied, %d failed (first RVA %d) [%s]' % (len(_failed), _failed[0], _python))");
        sb.AppendLine("else:");
        sb.AppendLine("    print('NetScope: annotations applied [%s]' % _python)");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ Java (GhidraScript)

    private static string WriteJava(GhidraAnnotations ann, string className)
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
        sb.AppendLine($"public class {className} extends GhidraScript {{");
        sb.AppendLine("    private int failed;");
        sb.AppendLine("    private int firstFailedRva = -1;");
        sb.AppendLine();
        sb.AppendLine("    @Override");
        sb.AppendLine("    public void run() throws Exception {");
        sb.AppendLine("        Address base = currentProgram.getImageBase();");
        sb.AppendLine("        BookmarkManager bm = currentProgram.getBookmarkManager();");
        foreach (var f in ann.Functions)
            sb.AppendLine($"        nameFunction(base, {f.Rva}, {Jv(SymbolName(f.Name))}, {Jv(f.Signature)});");
        foreach (var f in ann.Funclets)
            sb.AppendLine($"        label(base, {f.Rva}, {Jv(FuncletName(f))}, {Jv("funclet of " + f.ParentName)});");
        foreach (var i in ann.Imports)
            sb.AppendLine($"        label(base, {i.Rva}, {Jv("imp_" + Sanitize(i.Name))}, {Jv(i.Name)});");
        foreach (var b in ann.Bookmarks)
            sb.AppendLine($"        bookmark(bm, base, {b.Rva}, {Jv(b.Category)}, {Jv(b.Note)});");
        sb.AppendLine("        if (failed > 0) println(\"NetScope: annotations applied, \" + failed + \" failed (first RVA \" + firstFailedRva + \")\");");
        sb.AppendLine("        else println(\"NetScope: annotations applied\");");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    // One bad entry must not stop the rest.");
        sb.AppendLine("    private void fail(int rva) {");
        sb.AppendLine("        if (failed++ == 0) firstFailedRva = rva;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private void nameFunction(Address base, int rva, String name, String signature) {");
        sb.AppendLine("        try {");
        sb.AppendLine("            Address addr = base.add(rva);");
        sb.AppendLine("            Function f = getFunctionAt(addr);");
        sb.AppendLine("            if (f == null) f = createFunction(addr, name);");
        sb.AppendLine("            if (f != null) {");
        sb.AppendLine("                f.setName(name, SourceType.USER_DEFINED);   // idempotent: updates in place");
        sb.AppendLine("                if (signature != null && !signature.isEmpty()) setPlateComment(addr, signature);");
        sb.AppendLine("            } else {");
        sb.AppendLine("                createLabel(addr, name, true);");
        sb.AppendLine("            }");
        sb.AppendLine("        } catch (Exception e) {");
        sb.AppendLine("            fail(rva);");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private void label(Address base, int rva, String name, String comment) {");
        sb.AppendLine("        try {");
        sb.AppendLine("            Address addr = base.add(rva);");
        sb.AppendLine("            createLabel(addr, name, true);   // idempotent: same name is reused");
        sb.AppendLine("            if (comment != null && !comment.isEmpty()) setEOLComment(addr, comment);");
        sb.AppendLine("        } catch (Exception e) {");
        sb.AppendLine("            fail(rva);");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private void bookmark(BookmarkManager bm, Address base, int rva, String category, String note) {");
        sb.AppendLine("        try {");
        sb.AppendLine("            Address addr = base.add(rva);");
        sb.AppendLine("            for (Bookmark b : bm.getBookmarks(addr)) {");
        sb.AppendLine("                if (b.getType().getTypeString().equals(\"Note\") && b.getCategory().equals(category))");
        sb.AppendLine("                    bm.removeBookmark(b);   // idempotent: drop a prior run's bookmark first");
        sb.AppendLine("            }");
        sb.AppendLine("            createBookmark(addr, category, note);");
        sb.AppendLine("        } catch (Exception e) {");
        sb.AppendLine("            fail(rva);");
        sb.AppendLine("        }");
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

    /// <summary>
    /// Python string literal (None for null). Non-ASCII becomes \uXXXX so the file stays ASCII; the u prefix makes
    /// \uXXXX a Unicode escape in Jython (Python 2.7, Ghidra's default) as well as in Python 3 (Ghidrathon).
    /// </summary>
    private static string Py(string? s) => s == null ? "None" : "u\"" + Escape(s) + "\"";

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
