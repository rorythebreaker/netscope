using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.InteropServices;
using System.Text;
using ClrDebug;
using NetScope.Metadata;

namespace NetScope.Debugging;

/// <summary>
/// Formats values read from the debugged process for display (TZ-NetScope-2 §3.3). Strictly read-only: it
/// reads bytes and dereferences references, and never calls a method, getter or func-eval. Untrusted values
/// are bounded — strings truncated, collections capped, nesting limited — and escaped, so a hostile value
/// cannot flood the output or inject terminal escapes.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class DebugValues
{
    private const int MaxString = 1000;
    private const int MaxDepth = 3;

    private readonly Func<string, MetadataReader?> _metadata;

    public DebugValues(Func<string, MetadataReader?> metadata) => _metadata = metadata;

    public string Format(CorDebugValue value) => Format(value, 0);

    private string Format(CorDebugValue value, int depth)
    {
        try
        {
            // Unwrap references; report null without dereferencing.
            if (value is CorDebugReferenceValue reference)
            {
                if (reference.IsNull) return "null";
                value = reference.Dereference();
            }

            switch (value.Type)
            {
                case CorElementType.Boolean: return ReadPrimitive(value, p => Marshal.ReadByte(p) != 0 ? "true" : "false");
                case CorElementType.Char: return ReadPrimitive(value, p => "'" + Escape(((char)(ushort)Marshal.ReadInt16(p)).ToString()) + "'");
                case CorElementType.I1: return ReadPrimitive(value, p => ((sbyte)Marshal.ReadByte(p)).ToString());
                case CorElementType.U1: return ReadPrimitive(value, p => Marshal.ReadByte(p).ToString());
                case CorElementType.I2: return ReadPrimitive(value, p => Marshal.ReadInt16(p).ToString());
                case CorElementType.U2: return ReadPrimitive(value, p => ((ushort)Marshal.ReadInt16(p)).ToString());
                case CorElementType.I4: return ReadPrimitive(value, p => Marshal.ReadInt32(p).ToString());
                case CorElementType.U4: return ReadPrimitive(value, p => ((uint)Marshal.ReadInt32(p)).ToString());
                case CorElementType.I8: return ReadPrimitive(value, p => Marshal.ReadInt64(p).ToString());
                case CorElementType.U8: return ReadPrimitive(value, p => ((ulong)Marshal.ReadInt64(p)).ToString());
                case CorElementType.R4: return ReadPrimitive(value, p => BitConverter.Int32BitsToSingle(Marshal.ReadInt32(p)).ToString("R"));
                case CorElementType.R8: return ReadPrimitive(value, p => BitConverter.Int64BitsToDouble(Marshal.ReadInt64(p)).ToString("R"));
                case CorElementType.I: return ReadPrimitive(value, p => (IntPtr.Size == 8 ? Marshal.ReadInt64(p) : Marshal.ReadInt32(p)).ToString());
                case CorElementType.U: return ReadPrimitive(value, p => (IntPtr.Size == 8 ? (ulong)Marshal.ReadInt64(p) : (uint)Marshal.ReadInt32(p)).ToString());
                case CorElementType.String:
                    return value is CorDebugStringValue s ? "\"" + Escape(Truncate(SafeString(s))) + "\"" : "\"?\"";
                case CorElementType.SZArray or CorElementType.Array:
                    return value is CorDebugArrayValue a ? TypeName(value) + "[" + SafeCount(a) + "]" : "array";
                case CorElementType.Class or CorElementType.Object or CorElementType.ValueType:
                    return FormatObject(value, depth);
                default:
                    return "<" + value.Type + ">";
            }
        }
        catch (Exception ex)
        {
            return "<unavailable: " + ex.GetType().Name + ">";
        }
    }

    private string FormatObject(CorDebugValue value, int depth)
    {
        string name = TypeName(value);
        if (depth >= MaxDepth || value is not CorDebugObjectValue obj) return "{" + name + "}";
        // One bounded level of fields so `locals`/`args` are useful without unbounded recursion.
        var fields = ClassFields(obj);
        if (fields.Count == 0) return "{" + name + "}";
        var sb = new StringBuilder("{").Append(name);
        int shown = 0;
        foreach (var (fieldName, token) in fields)
        {
            if (shown++ >= 16) { sb.Append(", …"); break; }
            try
            {
                var fv = obj.GetFieldValue(obj.Class.Raw, new mdFieldDef(token));
                sb.Append(shown == 1 ? " " : ", ").Append(fieldName).Append('=').Append(Format(fv, depth + 1));
            }
            catch { /* skip a field that cannot be read */ }
        }
        return sb.Append('}').ToString();
    }

    /// <summary>Follows a `name.field.sub` path from a starting value, reading fields only (no getters).</summary>
    public string FormatPath(CorDebugValue value, IReadOnlyList<string> path, out string? error)
    {
        error = null;
        CorDebugValue current = value;
        for (int i = 0; i < path.Count; i++)
        {
            try
            {
                if (current is CorDebugReferenceValue r)
                {
                    if (r.IsNull) { error = "null reference before '" + path[i] + "'"; return "null"; }
                    current = r.Dereference();
                }
                if (current is not CorDebugObjectValue obj) { error = "'" + path[i] + "' is not a field of a reference/object"; return "?"; }
                var field = ClassFields(obj).FirstOrDefault(f => f.Name == path[i]);
                if (field.Token == 0) { error = "no field '" + path[i] + "'"; return "?"; }
                current = obj.GetFieldValue(obj.Class.Raw, new mdFieldDef(field.Token));
            }
            catch (Exception ex) { error = ex.Message; return "?"; }
        }
        return Format(current, 0);
    }

    private string TypeName(CorDebugValue value)
    {
        try
        {
            var cls = value is CorDebugObjectValue o ? o.Class : value.ExactType?.Class;
            if (cls == null) return "object";
            var md = _metadata(cls.Module.Name);
            if (md == null) return "object";
            return Names.TypeDefFullName(md, MetadataTokens.TypeDefinitionHandle((int)(cls.Token.Value & 0x00FFFFFF)));
        }
        catch { return "object"; }
    }

    private List<(string Name, int Token)> ClassFields(CorDebugObjectValue obj)
    {
        var list = new List<(string, int)>();
        try
        {
            var md = _metadata(obj.Class.Module.Name);
            if (md == null) return list;
            var type = md.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle((int)(obj.Class.Token.Value & 0x00FFFFFF)));
            foreach (var fh in type.GetFields())
            {
                var f = md.GetFieldDefinition(fh);
                if ((f.Attributes & System.Reflection.FieldAttributes.Static) != 0) continue; // instance fields only
                list.Add((md.GetString(f.Name), MetadataTokens.GetToken(fh)));
            }
        }
        catch { /* best effort */ }
        return list;
    }

    private static string ReadPrimitive(CorDebugValue value, Func<IntPtr, string> read)
    {
        if (value is not CorDebugGenericValue gv) return "?";
        int size = Math.Max(1, value.Size);
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try { gv.GetValue(buffer); return read(buffer); }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string SafeString(CorDebugStringValue s)
    {
        try { return s.GetString(s.Length) ?? ""; } catch { return ""; }
    }

    private static int SafeCount(CorDebugArrayValue a)
    {
        try { return a.TryGetCount(out int c) == HRESULT.S_OK ? c : 0; } catch { return 0; }
    }

    private static string Truncate(string s) => s.Length <= MaxString ? s : s[..MaxString] + "…(+" + (s.Length - MaxString) + ")";

    /// <summary>Escapes control characters and ESC so an untrusted value cannot inject terminal sequences.</summary>
    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\x1b': sb.Append("\\e"); break;
                default:
                    if (c < 0x20) sb.Append("\\x").Append(((int)c).ToString("x2"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
