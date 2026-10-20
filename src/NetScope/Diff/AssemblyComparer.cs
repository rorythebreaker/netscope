using System.Collections.Immutable;
using System.Globalization;
using System.Reflection.Metadata;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NetScope.Asm;
using NetScope.Audit;
using NetScope.Calls;
using NetScope.Catalog;
using NetScope.Cfg;
using NetScope.IL;
using NetScope.Loading;
using NetScope.Metadata;
using NetScope.Pseudo;

namespace NetScope.Diff;

public sealed class DiffOptions
{
    public MethodFilter Filter { get; init; } = MethodFilter.All;
    public bool Pseudo { get; init; }
    public IReadOnlyList<string> RefPaths { get; init; } = Array.Empty<string>();
    public TimeSpan DecompileTimeout { get; init; } = TimeSpan.FromSeconds(20);
    /// <summary>--audit: report findings that are new in the second version.</summary>
    public Auditor? Auditor { get; init; }
}

/// <summary>
/// Compares two versions of one assembly. Methods are matched by full name and signature, because tokens
/// change between builds. A method is "changed" when the hash of its IL differs, where the IL has tokens
/// replaced by full names and branch targets by instruction numbers, so a rebuild of the same code is equal.
/// </summary>
public sealed partial class AssemblyComparer
{
    private readonly DiffOptions _options;

    public AssemblyComparer(DiffOptions options) => _options = options;

    private sealed class Side
    {
        public required LoadedAssembly Asm { get; init; }
        public required MemberResolver Members { get; init; }
        public required IReadOnlyList<MethodModel> Methods { get; init; }
        public required Dictionary<string, MethodModel> ByKey { get; init; }
        public PseudoGenerator? Pseudo { get; set; }
    }

    public AssemblyDiff Compare(LoadedAssembly oldAsm, LoadedAssembly newAsm)
    {
        var result = new AssemblyDiff { Name = newAsm.Name, OldPath = oldAsm.FilePath, NewPath = newAsm.FilePath };
        using var oldRefs = new ReferenceResolver(new[] { oldAsm }, _options.RefPaths);
        using var newRefs = new ReferenceResolver(new[] { newAsm }, _options.RefPaths);
        var oldSide = Open(oldAsm, oldRefs);
        var newSide = Open(newAsm, newRefs);

        foreach (var (key, oldMethod) in oldSide.ByKey)
        {
            if (!oldMethod.Selected) continue;
            if (!newSide.ByKey.TryGetValue(key, out var newMethod))
                result.Methods.Add(Entry(key, oldMethod, MethodStatus.Removed));
            else
                result.Methods.Add(CompareMethod(key, oldSide, oldMethod, newSide, newMethod));
        }
        foreach (var (key, newMethod) in newSide.ByKey)
            if (newMethod.Selected && !oldSide.ByKey.ContainsKey(key))
                result.Methods.Add(Entry(key, newMethod, MethodStatus.Added));

        CompareMetadata(oldSide, newSide, result.Metadata);
        if (_options.Auditor != null) result.NewFindings.AddRange(NewFindings(oldSide, newSide, _options.Auditor));
        return result;
    }

    private Side Open(LoadedAssembly asm, ReferenceResolver references)
    {
        var members = new MemberResolver(asm.Metadata, asm.Name);
        var methods = new MethodCatalog().Build(asm, members, _options.Filter);
        var byKey = new Dictionary<string, MethodModel>(StringComparer.Ordinal);
        foreach (var m in methods)
        {
            var key = Identity(m);
            // Overloads that differ only in custom modifiers would collide; keep both under distinct keys.
            for (int i = 2; !byKey.TryAdd(key, m); i++) key = Identity(m) + "#" + i.ToString(CultureInfo.InvariantCulture);
        }
        var side = new Side { Asm = asm, Members = members, Methods = methods, ByKey = byKey };
        if (_options.Pseudo) side.Pseudo = new PseudoGenerator(asm, references.For(asm), members, false, _options.DecompileTimeout, new AbandonedDecompilations(4));
        return side;
    }

    /// <summary>The method key without the assembly name (the assembly may be renamed between versions).</summary>
    private static string Identity(MethodModel m)
    {
        int bar = m.Key.IndexOf('|', StringComparison.Ordinal);
        return bar < 0 ? m.Key : m.Key.Substring(bar + 1);
    }

    private static MethodDiff Entry(string key, MethodModel m, MethodStatus status) =>
        new() { Key = key, FullName = m.FullName, Signature = m.Signature, Status = status };

    private static MethodDiff CompareMethod(string key, Side oldSide, MethodModel oldMethod, Side newSide, MethodModel newMethod)
    {
        try
        {
            var oldIL = oldMethod.HasBody ? MethodIL.Load(oldMethod, oldSide.Members, out _, out _) : null;
            var newIL = newMethod.HasBody ? MethodIL.Load(newMethod, newSide.Members, out _, out _) : null;
            var oldListing = oldIL == null ? null : Normalize(oldIL, oldSide.Members);
            var newListing = newIL == null ? null : Normalize(newIL, newSide.Members);
            if (oldListing?.Hash == newListing?.Hash) return Entry(key, newMethod, MethodStatus.Unchanged);

            var oldLines = oldListing?.Keys ?? Array.Empty<string>();
            var newLines = newListing?.Keys ?? Array.Empty<string>();
            var edits = LineDiff.Compute(oldLines, newLines);
            var ilDiff = LineDiff.Unified(oldListing?.Shown ?? Array.Empty<string>(), newListing?.Shown ?? Array.Empty<string>(), edits,
                "old/" + oldMethod.FullName, "new/" + newMethod.FullName);

            var oldCalls = oldIL == null ? new List<string>() : new CallAnalyzer().Analyze(oldIL, oldSide.Members).Select(c => c.Display).ToList();
            var newCalls = newIL == null ? new List<string>() : new CallAnalyzer().Analyze(newIL, newSide.Members).Select(c => c.Display).ToList();
            var (oldCfg, newCfg) = (Cfg(oldIL, oldSide.Members), Cfg(newIL, newSide.Members));

            string? pseudoDiff = null;
            if (oldSide.Pseudo != null && newSide.Pseudo != null)
            {
                var a = Lines(oldSide.Pseudo.Generate(oldMethod, oldIL, oldCfg).Text);
                var b = Lines(newSide.Pseudo.Generate(newMethod, newIL, newCfg).Text);
                pseudoDiff = LineDiff.Unified(a, b, LineDiff.Compute(a, b), "old/" + oldMethod.FullName, "new/" + newMethod.FullName);
            }

            return new MethodDiff
            {
                Key = key,
                FullName = newMethod.FullName,
                Signature = newMethod.Signature,
                Status = MethodStatus.Changed,
                ILDiff = ilDiff,
                PseudoDiff = pseudoDiff,
                CallsAdded = MultisetMinus(newCalls, oldCalls),
                CallsRemoved = MultisetMinus(oldCalls, newCalls),
                OldComplexity = oldCfg?.CyclomaticComplexity,
                NewComplexity = newCfg?.CyclomaticComplexity,
                OldBlocks = oldCfg?.Blocks.Count,
                NewBlocks = newCfg?.Blocks.Count,
            };
        }
        finally
        {
            oldMethod.ReleaseBody();
            newMethod.ReleaseBody();
        }
    }

    private static ControlFlowGraph? Cfg(MethodIL? il, MemberResolver members)
    {
        if (il == null) return null;
        try { return new CfgBuilder().Build(il, members); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { return null; }
    }

    private static string[] Lines(string text) => text.Replace("\r\n", "\n").Split('\n');

    /// <summary>Distinct items of <paramref name="a"/> that occur more often than in <paramref name="b"/>.</summary>
    private static List<string> MultisetMinus(List<string> a, List<string> b)
    {
        var counts = b.GroupBy(x => x, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var g in a.GroupBy(x => x, StringComparer.Ordinal))
            if (g.Count() > counts.GetValueOrDefault(g.Key)) result.Add(g.Key);
        return result;
    }

    // ------------------------------------------------------------------ normalized IL

    internal sealed record NormalizedIL(string[] Keys, string[] Shown, string Hash);

    [GeneratedRegex(@"IL_([0-9A-F]{4,8})", RegexOptions.CultureInvariant)]
    private static partial Regex OffsetLabel();

    /// <summary>
    /// Lines to compare (opcode and token-free operand, branch targets as instruction numbers), lines to show
    /// (with the real offsets) and a hash that also covers locals and exception clauses.
    /// </summary>
    internal static NormalizedIL Normalize(MethodIL il, MemberResolver members)
    {
        var listing = new AsmProvider(members, false, Array.Empty<string>(), TimeSpan.FromSeconds(1)).GetIL(il);
        var ordinal = new Dictionary<int, int>();
        for (int i = 0; i < il.Instructions.Count; i++) ordinal[il.Instructions[i].Offset] = i;
        string Label(Match m)
        {
            int offset = int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return ordinal.TryGetValue(offset, out var n) ? "L" + n.ToString(CultureInfo.InvariantCulture) : "Lend";
        }

        var keys = new string[listing.Lines.Count];
        var shown = new string[listing.Lines.Count];
        for (int i = 0; i < listing.Lines.Count; i++)
        {
            var line = listing.Lines[i];
            var ins = il.Instructions[i];
            var operand = ins.IsBranch || ins.IsSwitch ? OffsetLabel().Replace(line.Operand, Label) : line.Operand;
            keys[i] = operand.Length == 0 ? line.OpCode : line.OpCode + " " + operand;
            shown[i] = line.Operand.Length == 0 ? $"{line.Label}  {line.OpCode}" : $"{line.Label}  {line.OpCode,-10} {line.Operand}";
        }

        var hash = new StringBuilder();
        hash.Append("maxstack ").Append(listing.MaxStack).Append(listing.LocalsInit ? " init" : "").Append('\n');
        foreach (var local in listing.Locals) hash.Append(".local ").Append(local).Append('\n');
        foreach (var clause in listing.ExceptionClauses) hash.Append(OffsetLabel().Replace(clause, Label)).Append('\n');
        foreach (var k in keys) hash.Append(k).Append('\n');
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hash.ToString())));
        return new NormalizedIL(keys, shown, digest);
    }

    // ------------------------------------------------------------------ metadata

    private static void CompareMetadata(Side oldSide, Side newSide, List<MetadataChange> changes)
    {
        Sets(changes, "type", Types(oldSide.Asm.Metadata), Types(newSide.Asm.Metadata));
        Sets(changes, "field", Fields(oldSide), Fields(newSide));
        Sets(changes, "attribute", Attributes(oldSide), Attributes(newSide));
        Versions(changes, "reference", References(oldSide.Asm.Metadata), References(newSide.Asm.Metadata));
        Versions(changes, "pinvoke", PInvokes(oldSide), PInvokes(newSide));
    }

    private static void Sets(List<MetadataChange> changes, string kind, ISet<string> before, ISet<string> after)
    {
        foreach (var item in before.Except(after).Order(StringComparer.Ordinal)) changes.Add(new MetadataChange(kind, "removed", item));
        foreach (var item in after.Except(before).Order(StringComparer.Ordinal)) changes.Add(new MetadataChange(kind, "added", item));
    }

    /// <summary>Items with a value (version, DLL entry): added, removed or changed "old -> new".</summary>
    private static void Versions(List<MetadataChange> changes, string kind, IDictionary<string, string> before, IDictionary<string, string> after)
    {
        foreach (var name in before.Keys.Union(after.Keys).Order(StringComparer.Ordinal))
        {
            bool inOld = before.TryGetValue(name, out var a), inNew = after.TryGetValue(name, out var b);
            if (inOld && !inNew) changes.Add(new MetadataChange(kind, "removed", name, a));
            else if (!inOld && inNew) changes.Add(new MetadataChange(kind, "added", name, b));
            else if (a != b) changes.Add(new MetadataChange(kind, "changed", name, $"{a} -> {b}"));
        }
    }

    private static HashSet<string> Types(MetadataReader md) =>
        md.TypeDefinitions.Select(h => Names.TypeDefFullName(md, h)).Where(n => n != "<Module>").ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> Fields(Side side)
    {
        var md = side.Asm.Metadata;
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var h in md.FieldDefinitions)
        {
            var f = side.Members.ResolveField(h);
            var type = Names.TypeDefFullName(md, md.GetFieldDefinition(h).GetDeclaringType());
            set.Add($"{type}::{f.Name} : {f.FieldType}");
        }
        return set;
    }

    /// <summary>"assembly: [Attr(args)]", "type T: [...]", "method T::M(sig): [...]".</summary>
    private static HashSet<string> Attributes(Side side)
    {
        var md = side.Asm.Metadata;
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (md.IsAssembly)
            foreach (var a in AttributeTexts(md, md.GetAssemblyDefinition().GetCustomAttributes())) set.Add("assembly: " + a);
        foreach (var h in md.TypeDefinitions)
            foreach (var a in AttributeTexts(md, md.GetTypeDefinition(h).GetCustomAttributes())) set.Add($"type {Names.TypeDefFullName(md, h)}: {a}");
        foreach (var m in side.Methods)
            foreach (var a in AttributeTexts(md, md.GetMethodDefinition(m.Handle).GetCustomAttributes())) set.Add($"method {m.FullName} {m.Signature}: {a}");
        return set;
    }

    private static IEnumerable<string> AttributeTexts(MetadataReader md, CustomAttributeHandleCollection attributes)
    {
        foreach (var h in attributes)
        {
            var attr = md.GetCustomAttribute(h);
            var type = Names.GetAttributeTypeName(md, attr) ?? "?";
            string args;
            try
            {
                var value = attr.DecodeValue(AttributeTypeProvider.Instance);
                args = string.Join(", ", value.FixedArguments.Select(Format).Concat(value.NamedArguments.Select(n => n.Name + " = " + Format(new CustomAttributeTypedArgument<string>(n.Type, n.Value)))));
            }
            catch (Exception ex) when (ex is BadImageFormatException or ArgumentException or InvalidOperationException)
            {
                // Arguments of an unknown enum type: compare the raw blob.
                args = "0x" + Convert.ToHexString(md.GetBlobBytes(attr.Value));
            }
            yield return $"[{type}({args})]";
        }
    }

    private static string Format(CustomAttributeTypedArgument<string> a) => a.Value switch
    {
        null => "null",
        string s => Names.EscapeString(s),
        ImmutableArray<CustomAttributeTypedArgument<string>> items => "{" + string.Join(", ", items.Select(Format)) + "}",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        var v => v.ToString() ?? "",
    };

    private static Dictionary<string, string> References(MetadataReader md)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in md.AssemblyReferences)
        {
            var r = md.GetAssemblyReference(h);
            map[md.GetString(r.Name)] = r.Version.ToString();
        }
        return map;
    }

    private static Dictionary<string, string> PInvokes(Side side)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var m in side.Methods)
            if (m.PInvoke != null) map[$"{m.FullName} {m.Signature}"] = m.PInvoke.ToString();
        return map;
    }

    /// <summary>Signature types as plain names for attribute decoding; enums are assumed to be Int32.</summary>
    private sealed class AttributeTypeProvider : ICustomAttributeTypeProvider<string>
    {
        public static readonly AttributeTypeProvider Instance = new();
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSystemType() => "System.Type";
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => Names.TypeDefFullName(reader, handle);
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => Names.TypeRefFullName(reader, handle);
        public string GetTypeFromSerializedName(string name) => name;
        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;
        public bool IsSystemType(string type) => type == "System.Type";
    }

    // ------------------------------------------------------------------ audit

    /// <summary>Findings of the new version minus those of the old one (by rule, method identity and message).</summary>
    private static IEnumerable<Finding> NewFindings(Side oldSide, Side newSide, Auditor auditor)
    {
        var old = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var f in Findings(oldSide, auditor))
        {
            var k = FindingKey(f);
            old[k] = old.GetValueOrDefault(k) + 1;
        }
        foreach (var f in Findings(newSide, auditor))
        {
            var k = FindingKey(f);
            if (old.TryGetValue(k, out var n) && n > 0) old[k] = n - 1;
            else yield return f;
        }
    }

    private static string FindingKey(Finding f) => f.RuleId + "|" + Identity(f.Method) + "|" + f.Message;

    private static List<Finding> Findings(Side side, Auditor auditor)
    {
        var definitions = side.Methods.ToDictionary(m => m.Handle);
        var list = new List<Finding>();
        foreach (var m in side.Methods.Where(m => m.Selected))
        {
            try { list.AddRange(auditor.Check(m, side.Members, definitions, out _)); }
            catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException) { }
            finally { m.ReleaseBody(); }
        }
        return list;
    }
}
