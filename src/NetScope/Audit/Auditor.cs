using System.Reflection.Metadata;
using System.Text.RegularExpressions;
using NetScope.Catalog;
using NetScope.IL;
using NetScope.Input;
using NetScope.Loading;
using NetScope.Metadata;

namespace NetScope.Audit;

/// <summary>All rules of NetScope, found by reflection and ordered by ID.</summary>
public static class RuleSet
{
    public static IReadOnlyList<IRule> All { get; } = typeof(IRule).Assembly.GetTypes()
        .Where(t => typeof(IRule).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
        .Select(t => (IRule)Activator.CreateInstance(t)!)
        .OrderBy(r => r.Id, StringComparer.Ordinal)
        .ToList();

    /// <summary>Rules selected by --rules (all when empty). Throws <see cref="ArgumentException"/> for an unknown ID.</summary>
    public static IReadOnlyList<IRule> Select(IEnumerable<string> ids)
    {
        var wanted = ids.SelectMany(i => i.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(i => i.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0) return All;
        var unknown = wanted.Where(w => All.All(r => r.Id != w)).ToList();
        if (unknown.Count > 0) throw new ArgumentException($"unknown rule {string.Join(", ", unknown)}. Known: {string.Join(", ", All.Select(r => r.Id))}");
        return All.Where(r => wanted.Contains(r.Id)).ToList();
    }

    public static bool TryParseLevel(string text, out Severity level) =>
        Enum.TryParse(text, ignoreCase: true, out level) && Enum.IsDefined(level);
}

/// <summary>
/// Suppressed findings: [SuppressMessage(category, "NS004...")] or [UnconditionalSuppressMessage] on the method, its types or the assembly,
/// and lines "NS004 MyApp.Legacy.*" in netscope.suppress files (in the current directory and next to the assembly).
/// </summary>
public sealed class Suppressions
{
    public const string FileName = "netscope.suppress";
    private readonly List<(string Rule, Regex Method)> _entries = new();

    public static Suppressions Load(IEnumerable<string> files)
    {
        var s = new Suppressions();
        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(file)) continue;
            foreach (var raw in File.ReadAllLines(file))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                var mask = parts.Length > 1 ? parts[1].Trim() : "*";
                s._entries.Add((parts[0].ToUpperInvariant(), InputResolver.GlobToRegex(mask.Replace('/', '+'))));
            }
        }
        return s;
    }

    public bool FromFile(Finding f) =>
        _entries.Any(e => (e.Rule == f.RuleId || e.Rule == "*") && e.Method.IsMatch(f.Method.FullName));

    /// <summary>SuppressMessageAttribute whose checkId starts with the rule ID ("NS004" or "NS004:Unsafe...").</summary>
    public static bool ByAttribute(MetadataReader md, MethodModel m, string ruleId)
    {
        if (Suppressed(md, md.GetMethodDefinition(m.Handle).GetCustomAttributes(), ruleId)) return true;
        for (var t = m.DeclaringTypeHandle; !t.IsNil; t = md.GetTypeDefinition(t).GetDeclaringType())
            if (Suppressed(md, md.GetTypeDefinition(t).GetCustomAttributes(), ruleId)) return true;
        return md.IsAssembly && Suppressed(md, md.GetAssemblyDefinition().GetCustomAttributes(), ruleId) ||
               Suppressed(md, md.GetModuleDefinition().GetCustomAttributes(), ruleId);
    }

    private static bool Suppressed(MetadataReader md, CustomAttributeHandleCollection attributes, string ruleId)
    {
        foreach (var h in attributes)
        {
            var attr = md.GetCustomAttribute(h);
            // SuppressMessage is compiled only with CODE_ANALYSIS defined; UnconditionalSuppressMessage always is.
            var type = Names.GetAttributeTypeName(md, attr);
            if (type is not ("System.Diagnostics.CodeAnalysis.SuppressMessageAttribute" or "System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessageAttribute")) continue;
            try
            {
                var value = attr.DecodeValue(new StringAttributeProvider());
                if (value.FixedArguments.Length >= 2 && value.FixedArguments[1].Value is string checkId &&
                    checkId.StartsWith(ruleId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch (BadImageFormatException) { }
        }
        return false;
    }

    /// <summary>Decodes the two string arguments of SuppressMessageAttribute; other types are not needed.</summary>
    private sealed class StringAttributeProvider : ICustomAttributeTypeProvider<object?>
    {
        public object? GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode;
        public object? GetSystemType() => typeof(Type);
        public object? GetSZArrayType(object? elementType) => null;
        public object? GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => null;
        public object? GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => null;
        public object? GetTypeFromSerializedName(string name) => null;
        public PrimitiveTypeCode GetUnderlyingEnumType(object? type) => PrimitiveTypeCode.Int32;
        public bool IsSystemType(object? type) => type is Type;
    }
}

/// <summary>Runs the selected rules over methods of one assembly.</summary>
public sealed class Auditor
{
    private readonly IReadOnlyList<IRule> _rules;
    private readonly Severity _minLevel;
    private readonly Suppressions _suppressions;

    public Auditor(IReadOnlyList<IRule> rules, Severity minLevel, Suppressions suppressions)
    {
        _rules = rules;
        _minLevel = minLevel;
        _suppressions = suppressions;
    }

    public IReadOnlyList<IRule> Rules => _rules;

    /// <summary>Findings of one method that pass --min-level and are not suppressed.</summary>
    public IReadOnlyList<Finding> Check(MethodModel method, MemberResolver resolver, IReadOnlyDictionary<MethodDefinitionHandle, MethodModel> definitions, out int suppressed)
    {
        suppressed = 0;
        MethodIL? il = null;
        if (method.HasBody) il = MethodIL.Load(method, resolver, out _, out _);

        var raw = new List<Finding>();
        var context = new RuleContext(method, il, resolver,
            h => definitions.TryGetValue(h, out var d) ? MethodIL.Load(d, resolver, out _, out _) : null, raw);
        foreach (var rule in _rules)
        {
            try
            {
                rule.Check(context);
            }
            catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or InvalidCastException or IndexOutOfRangeException)
            {
                // A malformed body must not stop the other rules.
            }
        }

        var result = new List<Finding>();
        foreach (var f in raw)
        {
            if (f.Level < _minLevel) continue;
            if (_suppressions.FromFile(f) || Suppressions.ByAttribute(resolver.Metadata, method, f.RuleId))
            {
                suppressed++;
                continue;
            }
            result.Add(f);
        }
        // IL order; declaration-level findings (no offset) first.
        result.Sort((a, b) => (a.Offset ?? -1).CompareTo(b.Offset ?? -1) is var c && c != 0 ? c : string.CompareOrdinal(a.RuleId, b.RuleId));
        return result;
    }

    /// <summary>netscope.suppress in the current directory and next to each assembly.</summary>
    public static IEnumerable<string> SuppressionFiles(IEnumerable<LoadedAssembly> assemblies) =>
        new[] { Path.Combine(Directory.GetCurrentDirectory(), Suppressions.FileName) }
            .Concat(assemblies.Select(a => Path.Combine(Path.GetDirectoryName(a.FilePath) ?? ".", Suppressions.FileName)));
}
