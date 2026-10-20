using System.Globalization;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using NetScope.Catalog;
using NetScope.Cfg;
using NetScope.IL;
using NetScope.Loading;
using NetScope.Metadata;

namespace NetScope.Obfuscation;

public enum ObfuscationVerdict { No, Possible, Yes }

/// <summary>One observed sign. Strong signs (an obfuscator's own attribute) decide the verdict alone.</summary>
public sealed record ObfuscationSign(string Kind, string Detail, bool Strong = false);

public sealed class ObfuscationReport
{
    public required ObfuscationVerdict Verdict { get; init; }
    public required IReadOnlyList<ObfuscationSign> Signs { get; init; }
    /// <summary>Recognized obfuscator, when an attribute names it.</summary>
    public string? Obfuscator { get; init; }

    public string VerdictText => Verdict switch { ObfuscationVerdict.Yes => "yes", ObfuscationVerdict.Possible => "possible", _ => "no" };

    /// <summary>NetScope does not deobfuscate; it suggests an external tool instead.</summary>
    public string? Hint => Verdict == ObfuscationVerdict.Yes
        ? "deobfuscate the file with an external tool such as de4dot before analyzing it"
        : null;
}

/// <summary>
/// Looks for the signs of obfuscation listed in TZ 4.3. Only metadata and IL are read. Verdict: "yes" for an
/// obfuscator attribute or two or more signs, "possible" for one sign, "no" otherwise.
/// </summary>
public static class ObfuscationDetector
{
    /// <summary>Attribute (type) names that obfuscators leave in the output, and the product they identify.</summary>
    private static readonly (string Name, string Obfuscator)[] Markers =
    {
        ("ConfusedByAttribute", "ConfuserEx"),
        ("DotfuscatorAttribute", "Dotfuscator"),
        ("SmartAssembly.Attributes.", "SmartAssembly"),
        ("PoweredByAttribute", "SmartAssembly"),
        ("BabelObfuscatorAttribute", "Babel Obfuscator"),
        ("BabelAttribute", "Babel Obfuscator"),
        ("ProtectedWithCryptoObfuscatorAttribute", "Crypto Obfuscator"),
        ("ObfuscatedByGoliath", "Goliath.NET"),
        ("ProcessedByXenocode", "Xenocode"),
        ("YanoAttribute", "Yano"),
        ("NineRays.Obfuscator", "Spices.Net"),
        ("ObfuscatedByAgileDotNetAttribute", "Agile.NET"),
    };

    internal const double UnreadableNameShare = 0.3;
    internal const int FlatSwitchCases = 10;

    public static ObfuscationReport Analyze(LoadedAssembly assembly, MemberResolver resolver, IReadOnlyList<MethodModel> methods)
    {
        var md = assembly.Metadata;
        var signs = new List<ObfuscationSign>();

        string? obfuscator = FindMarker(md, out var marker);
        if (obfuscator != null) signs.Add(new ObfuscationSign("attribute", $"{marker} ({obfuscator})", Strong: true));

        var (unreadable, total) = CountUnreadableNames(md);
        if (total >= 10 && unreadable > total * UnreadableNameShare)
            signs.Add(new ObfuscationSign("names", $"{unreadable} of {total} names ({100.0 * unreadable / total:0}%) are unprintable, Unicode noise or one character"));

        int flat = 0, corrupt = 0, proxies = 0, bodies = 0, ldstr = 0;
        var decoderCalls = new Dictionary<string, int>(StringComparer.Ordinal);
        var cfg = new CfgBuilder();
        foreach (var m in methods)
        {
            if (!m.HasBody) continue;
            bodies++;
            MethodIL? il;
            try
            {
                il = MethodIL.Load(m, resolver, out _, out _);
            }
            catch (BadImageFormatException)
            {
                il = null;
            }
            if (il == null || HasInvalidTokens(md, il))
            {
                corrupt++;
                m.ReleaseBody();
                continue;
            }

            var list = il.Instructions;
            for (int i = 0; i < list.Count; i++)
            {
                var ins = list[i];
                if (ins.OpCode.Value == OpCodes.Ldstr.Value) ldstr++;
                else if (ins.OpCode.Value == OpCodes.Call.Value && i > 0 && IsIntConstant(list[i - 1]))
                {
                    var target = resolver.ResolveMethod(ins.Handle);
                    if (!target.HasThis && target.ParameterCount == 1 && target.Parameters is "(int)" or "(uint)" && target.ReturnType is "string" or "T" or "!!0")
                        decoderCalls[target.Key] = decoderCalls.GetValueOrDefault(target.Key) + 1;
                }
            }
            if (IsProxy(m, list)) proxies++;
            if (list.Any(x => x.IsSwitch && x.SwitchTargets.Length >= FlatSwitchCases) && IsFlattened(cfg, il, resolver)) flat++;
            m.ReleaseBody();
        }

        if (flat > 0) signs.Add(new ObfuscationSign("control-flow", $"{flat} method(s) with a computed switch dispatcher of {FlatSwitchCases}+ cases inside a loop (control flow flattening)"));
        var decoder = decoderCalls.OrderByDescending(kv => kv.Value).FirstOrDefault();
        if (decoder.Value >= 10 && ldstr * 4 <= decoder.Value)
            signs.Add(new ObfuscationSign("strings", $"{ldstr} string literal(s) but {decoder.Value} calls of one static method with a number argument (string decryption)"));
        if (corrupt > 0) signs.Add(new ObfuscationSign("bodies", $"{corrupt} method bod{(corrupt == 1 ? "y" : "ies")} cannot be decoded or refer to nonexistent tokens"));
        if (proxies >= 20 && proxies * 10 >= bodies)
            signs.Add(new ObfuscationSign("proxies", $"{proxies} of {bodies} methods are wrappers with unreadable names around a single call (proxy calls)"));

        var verdict = signs.Any(s => s.Strong) || signs.Count >= 2 ? ObfuscationVerdict.Yes
            : signs.Count == 1 ? ObfuscationVerdict.Possible
            : ObfuscationVerdict.No;
        return new ObfuscationReport { Verdict = verdict, Signs = signs, Obfuscator = obfuscator };
    }

    private static string? FindMarker(MetadataReader md, out string? marker)
    {
        marker = null;
        var names = new List<string>();
        // Attributes applied to the assembly, the module and the types, plus types defined in the module
        // (obfuscators inject their attribute class).
        if (md.IsAssembly) names.AddRange(AttributeNames(md, md.GetAssemblyDefinition().GetCustomAttributes()));
        names.AddRange(AttributeNames(md, md.GetModuleDefinition().GetCustomAttributes()));
        foreach (var h in md.TypeDefinitions)
        {
            var t = md.GetTypeDefinition(h);
            names.Add(Names.TypeDefFullName(md, h));
            names.AddRange(AttributeNames(md, t.GetCustomAttributes()));
        }
        foreach (var name in names)
            foreach (var (m, product) in Markers)
                if (name.Contains(m, StringComparison.Ordinal))
                {
                    marker = name;
                    return product;
                }
        return null;
    }

    private static IEnumerable<string> AttributeNames(MetadataReader md, CustomAttributeHandleCollection attributes)
    {
        foreach (var h in attributes)
        {
            string? name;
            try { name = Names.GetAttributeTypeName(md, md.GetCustomAttribute(h)); }
            catch (BadImageFormatException) { name = null; }
            if (name != null) yield return name;
        }
    }

    private static (int Unreadable, int Total) CountUnreadableNames(MetadataReader md)
    {
        int unreadable = 0, total = 0;
        void Count(string name)
        {
            // Compiler-generated and special names are not chosen by people or obfuscators.
            if (name.Length > 0 && (name[0] == '<' || name[0] == '.')) return;
            total++;
            if (IsUnreadable(name)) unreadable++;
        }
        foreach (var h in md.TypeDefinitions)
        {
            var t = md.GetTypeDefinition(h);
            var name = SafeString(md, t.Name);
            if (name != "<Module>") Count(Names.StripArity(name));
        }
        foreach (var h in md.MethodDefinitions) Count(SafeString(md, md.GetMethodDefinition(h).Name));
        foreach (var h in md.FieldDefinitions) Count(SafeString(md, md.GetFieldDefinition(h).Name));
        return (unreadable, total);
    }

    private static string SafeString(MetadataReader md, StringHandle h)
    {
        try { return md.GetString(h); }
        catch (BadImageFormatException) { return "�"; }
    }

    /// <summary>One character, or characters people do not type: control, format (e.g. RLO), private use, unassigned, separators.</summary>
    internal static bool IsUnreadable(string name)
    {
        if (name.Length <= 1) return true;
        foreach (var c in name)
        {
            switch (CharUnicodeInfo.GetUnicodeCategory(c))
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.Format:
                case UnicodeCategory.PrivateUse:
                case UnicodeCategory.OtherNotAssigned:
                case UnicodeCategory.Surrogate:
                case UnicodeCategory.SpaceSeparator:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                    return true;
            }
            if (c == '�') return true;
        }
        return false;
    }

    private static bool IsIntConstant(ILInstruction ins) => Audit.RuleContext.IntConstant(ins) != null;

    /// <summary>Token operands must point to an existing row (or into the #US heap for ldstr).</summary>
    private static bool HasInvalidTokens(MetadataReader md, MethodIL il)
    {
        int userStrings = md.GetHeapSize(HeapIndex.UserString);
        foreach (var ins in il.Instructions)
        {
            switch (ins.OpCode.OperandType)
            {
                case OperandType.InlineMethod:
                case OperandType.InlineField:
                case OperandType.InlineType:
                case OperandType.InlineTok:
                case OperandType.InlineSig:
                    if (!RowExists(md, ins.Token)) return true;
                    break;
                case OperandType.InlineString:
                    if ((ins.Token >> 24) != 0x70 || (ins.Token & 0xFFFFFF) >= userStrings) return true;
                    break;
            }
        }
        return false;
    }

    private static bool RowExists(MetadataReader md, int token)
    {
        int row = token & 0xFFFFFF;
        if (row == 0) return false;
        TableIndex? table = (token >> 24) switch
        {
            0x01 => TableIndex.TypeRef,
            0x02 => TableIndex.TypeDef,
            0x04 => TableIndex.Field,
            0x06 => TableIndex.MethodDef,
            0x0A => TableIndex.MemberRef,
            0x11 => TableIndex.StandAloneSig,
            0x1B => TableIndex.TypeSpec,
            0x2B => TableIndex.MethodSpec,
            _ => null,
        };
        return table is TableIndex t && row <= md.GetTableRowCount(t);
    }

    /// <summary>A static method with an unreadable name whose body only loads its arguments, makes one call and returns.</summary>
    private static bool IsProxy(MethodModel m, List<ILInstruction> list)
    {
        // Libraries have many readable forwarding helpers; obfuscators give their proxies meaningless names.
        if (!m.IsStatic || m.Kinds.Contains("compiler-generated") || list.Count < 2 || !IsUnreadable(m.Name)) return false;
        int calls = 0;
        for (int i = 0; i < list.Count; i++)
        {
            var op = list[i].OpCode;
            if (op.Value == OpCodes.Call.Value || op.Value == OpCodes.Callvirt.Value || op.Value == OpCodes.Newobj.Value) calls++;
            else if (op.Value == OpCodes.Ret.Value) { if (i != list.Count - 1) return false; }
            else if (!list[i].Name.StartsWith("ldarg", StringComparison.Ordinal)) return false;
        }
        return calls == 1;
    }

    /// <summary>A big switch inside a loop that most of its cases jump back to: the dispatcher of flattened control flow.</summary>
    private static bool IsFlattened(CfgBuilder builder, MethodIL il, MemberResolver resolver)
    {
        ControlFlowGraph cfg;
        try { cfg = builder.Build(il, resolver); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { return false; }
        foreach (var loop in cfg.Loops)
        {
            var dispatcher = loop.Body.FirstOrDefault(b => b.Last.IsSwitch && b.Last.SwitchTargets.Length >= FlatSwitchCases);
            if (dispatcher != null && loop.Latches.Count >= 3 && ComputesState(il.Instructions, dispatcher.Last)) return true;
        }
        return false;
    }

    /// <summary>
    /// The case number is computed from a state variable ("switch ((state ^ K) % N)"), unlike an interpreter
    /// that switches on an opcode read from data.
    /// </summary>
    private static bool ComputesState(List<ILInstruction> list, ILInstruction @switch)
    {
        int at = list.IndexOf(@switch);
        for (int i = Math.Max(0, at - 6); i < at; i++)
        {
            var v = list[i].OpCode.Value;
            if (v == OpCodes.Xor.Value || v == OpCodes.Rem.Value || v == OpCodes.Rem_Un.Value) return true;
        }
        return false;
    }
}
