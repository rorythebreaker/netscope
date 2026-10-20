using System.Text.RegularExpressions;
using NetScope.Input;

namespace NetScope.Catalog;

/// <summary>Selects methods by full-name masks (--filter) and by kind (--type). Several values are OR-ed.</summary>
public sealed class MethodFilter
{
    /// <summary>--type value -> method kinds it matches.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> TypeAliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["ctor"] = new[] { "ctor", "cctor" },
        ["cctor"] = new[] { "cctor" },
        ["finalizer"] = new[] { "finalizer" },
        ["property"] = new[] { "property-get", "property-set" },
        ["getter"] = new[] { "property-get" },
        ["setter"] = new[] { "property-set" },
        ["event"] = new[] { "event-add", "event-remove", "event-raise" },
        ["operator"] = new[] { "operator", "conversion" },
        ["conversion"] = new[] { "conversion" },
        ["abstract"] = new[] { "abstract" },
        ["virtual"] = new[] { "virtual", "override", "abstract" },
        ["override"] = new[] { "override" },
        ["sealed"] = new[] { "sealed" },
        ["static"] = new[] { "static" },
        ["instance"] = new[] { "instance" },
        ["pinvoke"] = new[] { "pinvoke" },
        ["extern"] = new[] { "pinvoke", "internalcall" },
        ["internalcall"] = new[] { "internalcall" },
        ["runtime"] = new[] { "runtime" },
        ["native"] = new[] { "native" },
        ["async"] = new[] { "async" },
        ["iterator"] = new[] { "iterator" },
        ["lambda"] = new[] { "lambda" },
        ["anonymous"] = new[] { "lambda" },
        ["local-function"] = new[] { "local-function" },
        ["localfunc"] = new[] { "local-function" },
        ["state-machine"] = new[] { "state-machine" },
        ["compiler-generated"] = new[] { "compiler-generated" },
        ["generic"] = new[] { "generic" },
        ["extension"] = new[] { "extension" },
        ["entry-point"] = new[] { "entry-point" },
        ["no-body"] = new[] { "no-body" },
        ["nobody"] = new[] { "no-body" },
    };

    private readonly Regex[] _masks;
    private readonly HashSet<string>? _kinds;

    public MethodFilter(IEnumerable<string> masks, IEnumerable<string> types)
    {
        _masks = masks.Select(m => InputResolver.GlobToRegex(m.Trim().Replace('/', '+'))).ToArray();
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in types.SelectMany(t => t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            if (!TypeAliases.TryGetValue(t, out var mapped)) throw new ArgumentException($"unknown method type '{t}'. Known: {string.Join(", ", TypeAliases.Keys)}");
            kinds.UnionWith(mapped);
        }
        _kinds = kinds.Count == 0 ? null : kinds;
    }

    public static MethodFilter All { get; } = new(Array.Empty<string>(), Array.Empty<string>());

    public bool IsEmpty => _masks.Length == 0 && _kinds == null;

    public bool Matches(MethodModel m)
    {
        if (_masks.Length > 0 && !_masks.Any(r => r.IsMatch(m.FullName))) return false;
        if (_kinds != null && !m.Kinds.Any(_kinds.Contains)) return false;
        return true;
    }
}
