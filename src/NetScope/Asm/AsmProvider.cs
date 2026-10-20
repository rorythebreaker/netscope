using System.Globalization;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using NetScope.Catalog;
using NetScope.IL;
using NetScope.Loading;
using NetScope.Metadata;
using NetScope.Native;

namespace NetScope.Asm;

public sealed record ILLine(int Offset, string OpCode, string Operand)
{
    public string Label => $"IL_{Offset:X4}";
}

public sealed class ILListing
{
    public int MaxStack { get; init; }
    public bool LocalsInit { get; init; }
    public List<string> Locals { get; init; } = new();
    public List<ILLine> Lines { get; init; } = new();
    public List<string> ExceptionClauses { get; init; } = new();
}

public interface IAsmProvider
{
    ILListing GetIL(MethodIL il);
    /// <summary>Native code for a batch of methods of one assembly (R2R from the image, JIT only with --allow-run).</summary>
    Dictionary<MethodModel, NativeCode> GetNative(LoadedAssembly asm, IReadOnlyList<MethodModel> methods);
}

public sealed class AsmProvider : IAsmProvider
{
    private readonly MemberResolver _resolver;
    private readonly bool _allowRun;
    private readonly IReadOnlyList<string> _refPaths;
    private readonly TimeSpan _runTimeout;

    public AsmProvider(MemberResolver resolver, bool allowRun, IReadOnlyList<string> refPaths, TimeSpan runTimeout)
    {
        _resolver = resolver;
        _allowRun = allowRun;
        _refPaths = refPaths;
        _runTimeout = runTimeout;
    }

    // ------------------------------------------------------------------ IL

    public ILListing GetIL(MethodIL il)
    {
        var lines = new List<ILLine>(il.Instructions.Count);
        foreach (var ins in il.Instructions)
        {
            string operand;
            try
            {
                operand = FormatOperand(ins, il);
            }
            catch (Exception)
            {
                operand = ins.Operand is int t ? $"0x{t:X8}" : "?";
            }
            lines.Add(new ILLine(ins.Offset, ins.Name, operand));
        }

        var locals = new List<string>();
        if (!il.Body.LocalSignature.IsNil)
        {
            try
            {
                var types = _resolver.Metadata.GetStandaloneSignature(il.Body.LocalSignature).DecodeLocalSignature(ILTypeProvider.Instance, il.Context);
                for (int i = 0; i < types.Length; i++) locals.Add($"[{i}] {types[i]} {il.LocalName(i)}");
            }
            catch (BadImageFormatException)
            {
                locals.Add("<unreadable local signature>");
            }
        }

        var clauses = new List<string>();
        foreach (var er in il.Body.ExceptionRegions)
        {
            var tryRange = $"IL_{er.TryOffset:X4} to IL_{er.TryOffset + er.TryLength:X4}";
            var handler = $"IL_{er.HandlerOffset:X4} to IL_{er.HandlerOffset + er.HandlerLength:X4}";
            clauses.Add(er.Kind switch
            {
                ExceptionRegionKind.Catch => $".try {tryRange} catch {SafeType(er.CatchType, il)} handler {handler}",
                ExceptionRegionKind.Filter => $".try {tryRange} filter IL_{er.FilterOffset:X4} handler {handler}",
                ExceptionRegionKind.Finally => $".try {tryRange} finally handler {handler}",
                _ => $".try {tryRange} fault handler {handler}",
            });
        }

        return new ILListing { MaxStack = il.Body.MaxStack, LocalsInit = il.Body.LocalVariablesInitialized, Locals = locals, Lines = lines, ExceptionClauses = clauses };
    }

    private string SafeType(EntityHandle handle, MethodIL il)
    {
        try { return _resolver.ResolveType(handle, il.Context).IL; }
        catch (Exception) { return "?"; }
    }

    private string FormatOperand(ILInstruction ins, MethodIL il)
    {
        var op = ins.OpCode;
        switch (op.OperandType)
        {
            case OperandType.InlineNone:
                return "";
            case OperandType.ShortInlineBrTarget:
            case OperandType.InlineBrTarget:
                return $"IL_{ins.BranchTarget:X4}";
            case OperandType.InlineSwitch:
                return "(" + string.Join(", ", ins.SwitchTargets.Select(t => $"IL_{t:X4}")) + ")";
            case OperandType.ShortInlineI:
                return ((int)ins.Operand!).ToString(CultureInfo.InvariantCulture);
            case OperandType.InlineI:
                return "0x" + ((int)ins.Operand!).ToString("x", CultureInfo.InvariantCulture);
            case OperandType.InlineI8:
                return "0x" + ((long)ins.Operand!).ToString("x", CultureInfo.InvariantCulture);
            case OperandType.ShortInlineR:
                return ((float)ins.Operand!).ToString("R", CultureInfo.InvariantCulture);
            case OperandType.InlineR:
                return ((double)ins.Operand!).ToString("R", CultureInfo.InvariantCulture);
            case OperandType.ShortInlineVar:
            case OperandType.InlineVar:
            {
                int index = (int)ins.Operand!;
                bool isArg = ins.Name.StartsWith("ldarg", StringComparison.Ordinal) || ins.Name.StartsWith("starg", StringComparison.Ordinal);
                return isArg ? il.ArgName(index) : il.LocalName(index);
            }
            case OperandType.InlineString:
                return Names.EscapeString(_resolver.Metadata.GetUserString((UserStringHandle)MetadataTokens.Handle(ins.Token)));
            case OperandType.InlineMethod:
                return _resolver.ResolveMethod(ins.Handle).IL;
            case OperandType.InlineField:
                return _resolver.ResolveField(ins.Handle).IL;
            case OperandType.InlineType:
                return _resolver.ResolveType(ins.Handle, il.Context).IL;
            case OperandType.InlineSig:
                return _resolver.ResolveStandaloneSignature((StandaloneSignatureHandle)ins.Handle, out _);
            case OperandType.InlineTok:
                return ins.Handle.Kind switch
                {
                    HandleKind.MethodDefinition or HandleKind.MethodSpecification => "method " + _resolver.ResolveMethod(ins.Handle).IL,
                    HandleKind.FieldDefinition => "field " + _resolver.ResolveField(ins.Handle).IL,
                    HandleKind.MemberReference => _resolver.Metadata.GetMemberReference((MemberReferenceHandle)ins.Handle).GetKind() == MemberReferenceKind.Method
                        ? "method " + _resolver.ResolveMethod(ins.Handle).IL
                        : "field " + _resolver.ResolveField(ins.Handle).IL,
                    _ => _resolver.ResolveType(ins.Handle, il.Context).IL,
                };
            default:
                return ins.Operand?.ToString() ?? "";
        }
    }

    // ------------------------------------------------------------------ native

    public Dictionary<MethodModel, NativeCode> GetNative(LoadedAssembly asm, IReadOnlyList<MethodModel> methods)
    {
        var result = new Dictionary<MethodModel, NativeCode>();
        var needJit = new List<MethodModel>();
        R2RReader? r2r = null;
        string? r2rError = null;
        if (asm.IsReadyToRun) r2r = R2RReader.TryOpen(asm, out r2rError);
        Dictionary<MethodDefinitionHandle, List<R2RInstance>>? instances = null;
        RuntimeFunctionTable? pdata = null;
        bool pdataRead = false;

        foreach (var m in methods)
        {
            if (m.IsNativeCode)
            {
                // C++/CLI: the body is machine code in the image; its size comes from the exception directory.
                if (!pdataRead)
                {
                    pdataRead = true;
                    try { pdata = RuntimeFunctionTable.FromPdata(asm); } catch (Exception) { pdata = null; }
                }
                result[m] = MixedModeNative(asm, m, pdata);
                continue;
            }
            if (!m.HasBody)
            {
                result[m] = NativeCode.Unavailable(m.PInvoke != null ? $"external function {m.PInvoke}" : "method has no body");
                continue;
            }
            string? reason = r2rError;
            if (r2r != null)
            {
                try
                {
                    var runtime = $"ReadyToRun {r2r.MajorVersion}.{r2r.MinorVersion}";
                    bool generic = m.Kinds.Contains("generic") || _resolver.Metadata.GetTypeDefinition(m.DeclaringTypeHandle).GetGenericParameters().Count > 0;
                    if (!r2r.CheckUsable(out reason))
                    {
                        // composite image or no entry points: fall through to JIT / unavailable
                    }
                    else if (generic)
                    {
                        instances ??= MapInstances(r2r, methods);
                        if (instances.TryGetValue(m.Handle, out var list) && list.Count > 0)
                        {
                            var parts = list.SelectMany(i => r2r.GetBodies(i.FunctionIndex)
                                .Select(b => (Name: b.Part == "main" ? i.Label : i.Label + " / " + b.Part, b.Rva, Code: r2r.ReadCode(b.Rva, b.Size))));
                            result[m] = NativeCode.FromImage(r2r.Architecture, "R2R", runtime, $"precompiled (R2R), {list.Count} instantiation(s)", parts);
                            continue;
                        }
                        reason = "no instantiation of this generic method was precompiled into the ReadyToRun image";
                    }
                    else if (r2r.TryGetMethodDef(MetadataTokens.GetRowNumber(m.Handle), out var bodies, out reason))
                    {
                        var parts = bodies.Select(b => (Name: b.Part, b.Rva, Code: r2r.ReadCode(b.Rva, b.Size)));
                        var tier = bodies.Count > 1 ? $"precompiled (R2R), {bodies.Count - 1} funclet(s)" : "precompiled (R2R)";
                        result[m] = NativeCode.FromImage(r2r.Architecture, "R2R", runtime, tier, parts);
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    reason = "cannot read R2R code: " + ex.Message;
                }
            }
            if (_allowRun) needJit.Add(m);
            else result[m] = NativeCode.Unavailable((reason != null ? reason + "; " : "") + "native code unavailable: JIT requires running the assembly (use --allow-run)");
        }

        if (needJit.Count > 0)
        {
            try
            {
                foreach (var (m, code) in JitRunner.Run(asm.FilePath, needJit, _refPaths, _runTimeout)) result[m] = code;
            }
            catch (Exception ex)
            {
                foreach (var m in needJit) result[m] = NativeCode.Unavailable("cannot start JIT helper: " + ex.Message);
            }
        }
        return result;
    }

    /// <summary>Groups precompiled instantiations by the method definition they instantiate.</summary>
    private Dictionary<MethodDefinitionHandle, List<R2RInstance>> MapInstances(R2RReader r2r, IReadOnlyList<MethodModel> methods)
    {
        var byKey = new Dictionary<string, MethodDefinitionHandle>(StringComparer.Ordinal);
        foreach (var m in methods) byKey.TryAdd(m.Key, m.Handle);
        var map = new Dictionary<MethodDefinitionHandle, List<R2RInstance>>();
        foreach (var i in r2r.Instances)
        {
            MethodDefinitionHandle def;
            if (i.Method.Kind == HandleKind.MethodDefinition) def = (MethodDefinitionHandle)i.Method;
            else if (i.Method.Kind == HandleKind.MemberReference && byKey.TryGetValue(_resolver.ResolveMethod(i.Method).Key, out var h)) def = h;
            else continue;
            if (!map.TryGetValue(def, out var list)) map[def] = list = new List<R2RInstance>();
            list.Add(i);
        }
        // Stable order: by native address.
        foreach (var list in map.Values) list.Sort((a, b) => a.FunctionIndex.CompareTo(b.FunctionIndex));
        return map;
    }

    private static NativeCode MixedModeNative(LoadedAssembly asm, MethodModel m, RuntimeFunctionTable? pdata)
    {
        const string outside = "native code outside the managed part (mixed-mode method)";
        var machine = R2RReader.NormalizeMachine(asm.PE.PEHeaders.CoffHeader.Machine);
        var arch = R2RReader.ArchitectureName(machine);
        var span = asm.GetRvaSpan(m.Rva);
        int index = pdata?.FindByBegin(m.Rva) ?? -1;
        int size = index >= 0 ? pdata!.Size(index) : 0;
        var tier = "native (C++/CLI), outside the managed part";
        if (index < 0 && machine is Machine.Amd64 or Machine.I386)
        {
            // Leaf functions and import thunks have no unwind data: estimate by a linear sweep.
            size = NativeDisassembler.EstimateFunctionSize(span.Slice(0, Math.Min(span.Length, 4096)), (ulong)m.Rva, machine == Machine.Amd64 ? 64 : 32);
            tier += ", size estimated (no unwind data)";
        }
        if (size <= 0 || size > span.Length)
            return NativeCode.Unavailable(outside + (index < 0 ? "; no unwind entry for this function, size unknown" : "; cannot determine its size"));
        return NativeCode.FromImage(arch, "image", null, tier, new[] { ("main", m.Rva, span.Slice(0, size).ToArray()) });
    }
}
