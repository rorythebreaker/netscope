using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text.RegularExpressions;
using NetScope.IL;
using NetScope.Input;
using NetScope.Metadata;
using NetScope.Symbols;

namespace NetScope.CrossRefs;

/// <summary>One place in a method that matched --uses, --find-string or --field-refs.</summary>
public sealed record CrossReference(int Offset, string Kind, string Target, SourcePoint? Source)
{
    /// <summary>uses, string or field.</summary>
    public required string Query { get; init; }
}

/// <summary>A field read or written by a method (--fields, --field-refs).</summary>
public sealed record FieldAccess(int Offset, string Access, FieldRef Field)
{
    /// <summary>"MyApp.Db.User::_name", the form --field-refs masks match.</summary>
    public string FullName => CrossReferenceQuery.TypeForMatching(Field.DeclaringType) + "::" + Field.Name;
}

/// <summary>
/// The cross-reference filters of a run. Several values of one option are OR-ed; different options are
/// AND-ed (like --filter and --type): a method is selected when it matches every option that was given.
/// </summary>
public sealed class CrossReferenceQuery
{
    /// <summary>Literals are cut to this length in the output.</summary>
    public const int MaxLiteralLength = 200;

    private readonly (Regex Regex, bool MemberMask)[] _uses;
    private readonly Func<string, bool>[] _strings;
    private readonly Regex[] _fields;

    public CrossReferenceQuery(IEnumerable<string> uses, IEnumerable<string> strings, bool regex, IEnumerable<string> fieldMasks)
    {
        _uses = uses.Select(u => (InputResolver.GlobToRegex(NormalizeMask(u)), u.Contains("::", StringComparison.Ordinal))).ToArray();
        _strings = strings.Select(s => StringMatcher(s, regex)).ToArray();
        _fields = fieldMasks.Select(f => InputResolver.GlobToRegex(NormalizeMask(f))).ToArray();
    }

    public static CrossReferenceQuery None { get; } = new(Array.Empty<string>(), Array.Empty<string>(), false, Array.Empty<string>());

    public bool IsEmpty => _uses.Length == 0 && _strings.Length == 0 && _fields.Length == 0;

    /// <summary>
    /// Matches of all given options in the method, in IL order. <paramref name="selected"/> is false when an
    /// option was given but nothing in the method matches it.
    /// </summary>
    public IReadOnlyList<CrossReference> Find(MethodIL il, MemberResolver resolver, out bool selected)
    {
        var symbols = il.Method.Assembly.Symbols;
        var found = new List<CrossReference>();
        bool usesHit = false, stringHit = false, fieldHit = false;
        foreach (var ins in il.Instructions)
        {
            var source = symbols?.At(il.Method.Handle, ins.Offset);
            var op = ins.OpCode;
            if (_uses.Length > 0 && op.OperandType == OperandType.InlineMethod)
            {
                var target = resolver.ResolveMethod(ins.Handle);
                if (MatchesUses(target))
                {
                    usesHit = true;
                    found.Add(new CrossReference(ins.Offset, ins.Name, target.Display, source) { Query = "uses" });
                }
            }
            else if (_strings.Length > 0 && op.Value == OpCodes.Ldstr.Value)
            {
                var value = Literal(ins, resolver);
                if (value != null && _strings.Any(m => m(value)))
                {
                    stringHit = true;
                    found.Add(new CrossReference(ins.Offset, ins.Name, Clip(value), source) { Query = "string" });
                }
            }
            else if (_fields.Length > 0 && FieldAccessAnalyzer.AccessOf(op) is { } access)
            {
                var field = new FieldAccess(ins.Offset, access, resolver.ResolveField(ins.Handle));
                if (_fields.Any(r => r.IsMatch(field.FullName)))
                {
                    fieldHit = true;
                    found.Add(new CrossReference(ins.Offset, access, field.FullName, source) { Query = "field" });
                }
            }
        }
        selected = (_uses.Length == 0 || usesHit) && (_strings.Length == 0 || stringHit) && (_fields.Length == 0 || fieldHit);
        return found;
    }

    private bool MatchesUses(MethodRef target)
    {
        var type = TypeForMatching(target.DeclaringType);
        var member = type + "::" + target.Name;
        // "Type::Method*" matches members; a plain type mask matches any member of the type.
        return _uses.Any(u => u.MemberMask ? u.Regex.IsMatch(member) : u.Regex.IsMatch(type));
    }

    /// <summary>"System.Collections.Generic.List&lt;int&gt;" -> "System.Collections.Generic.List".</summary>
    internal static string TypeForMatching(string declaringType)
    {
        var sb = new System.Text.StringBuilder(declaringType.Length);
        int depth = 0;
        foreach (var c in declaringType)
        {
            if (c == '<') depth++;
            else if (c == '>') depth--;
            else if (depth == 0) sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Nested types as '.', no generic arity: "MyApp.Outer+Inner`1::M" -> "MyApp.Outer.Inner::M".</summary>
    private static string NormalizeMask(string mask) =>
        Regex.Replace(mask.Trim().Replace('+', '.').Replace('/', '.'), @"`\d+", "");

    private static Func<string, bool> StringMatcher(string pattern, bool regex)
    {
        if (regex)
        {
            Regex r;
            try
            {
                r = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"invalid regular expression '{pattern}': {ex.Message}", ex);
            }
            return s =>
            {
                try { return r.IsMatch(s); }
                catch (RegexMatchTimeoutException) { return false; }
            };
        }
        if (pattern.Contains('*', StringComparison.Ordinal) || pattern.Contains('?', StringComparison.Ordinal))
        {
            var glob = InputResolver.GlobToRegex(pattern);
            return s => glob.IsMatch(s);
        }
        return s => s.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }

    internal static string? Literal(ILInstruction ins, MemberResolver resolver)
    {
        try
        {
            return resolver.Metadata.GetUserString((UserStringHandle)MetadataTokens.Handle(ins.Token));
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidCastException or ArgumentException)
        {
            return null;
        }
    }

    internal static string Clip(string value) => value.Length <= MaxLiteralLength ? value : string.Concat(value.AsSpan(0, MaxLiteralLength), "...");
}

/// <summary>Field reads (ldfld, ldsfld), address-taking (ldflda, ldsflda) and writes (stfld, stsfld).</summary>
public static class FieldAccessAnalyzer
{
    public static IReadOnlyList<FieldAccess> Analyze(MethodIL il, MemberResolver resolver)
    {
        var list = new List<FieldAccess>();
        foreach (var ins in il.Instructions)
            if (AccessOf(ins.OpCode) is { } access) list.Add(new FieldAccess(ins.Offset, access, resolver.ResolveField(ins.Handle)));
        return list;
    }

    internal static string? AccessOf(OpCode op)
    {
        var v = op.Value;
        if (v == OpCodes.Ldfld.Value || v == OpCodes.Ldsfld.Value) return "read";
        if (v == OpCodes.Ldflda.Value || v == OpCodes.Ldsflda.Value) return "address";
        if (v == OpCodes.Stfld.Value || v == OpCodes.Stsfld.Value) return "write";
        return null;
    }
}

/// <summary>One string literal of an assembly and the places that load it (--strings).</summary>
public sealed class StringLiteral
{
    public StringLiteral(string value) => Value = value;

    public string Value { get; }
    public List<(Catalog.MethodModel Method, int Offset)> Locations { get; } = new();
}
