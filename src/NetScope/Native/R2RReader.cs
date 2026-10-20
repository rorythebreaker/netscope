using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using NetScope.Loading;

namespace NetScope.Native;

/// <summary>One native code range of a method: the main body or a funclet (exception handler code).</summary>
public sealed record R2RFunction(string Part, int Rva, int Size);

/// <summary>A precompiled generic instantiation from the InstanceMethodEntryPoints section.</summary>
public sealed class R2RInstance
{
    /// <summary>MethodDef or MemberRef of this module; nil when the method belongs to another module.</summary>
    public EntityHandle Method { get; init; }
    /// <summary>C#-like instantiation, e.g. Generic&lt;__Canon&gt;::Map&lt;int&gt;.</summary>
    public required string Label { get; init; }
    public int FunctionIndex { get; init; }
    public bool IsUnboxingStub { get; init; }
    public bool IsInstantiatingStub { get; init; }
}

/// <summary>
/// Minimal ReadyToRun reader: finds the precompiled native code of methods (including generic
/// instantiations and funclets) directly in the image, without loading or running it.
/// </summary>
public sealed class R2RReader
{
    private const uint Signature = 0x00525452; // 'RTR'
    private const int SectionRuntimeFunctions = 102;
    private const int SectionMethodDefEntryPoints = 103;
    private const int SectionInstanceMethodEntryPoints = 109;
    private const int SectionOwnerCompositeExecutable = 116;
    private const uint FlagComponent = 0x20;

    // Machine values of R2R images are XOR-ed with an OS-specific constant.
    private static readonly ushort[] OsXor = { 0x0000, 0x7B79 /* Linux */, 0x4644 /* Apple */, 0xADC4 /* FreeBSD */, 0x1993 /* NetBSD */, 0x1992 /* SunOS */ };

    private readonly LoadedAssembly _asm;
    private readonly Dictionary<int, (int Rva, int Size)> _sections = new();
    private readonly object _indexLock = new();
    private RuntimeFunctionTable? _functions;
    private Dictionary<int, int>? _methodDefEntries;
    private List<R2RInstance>? _instances;
    private bool[]? _isEntryPoint;

    private R2RReader(LoadedAssembly asm) => _asm = asm;

    public ushort MajorVersion { get; private set; }
    public ushort MinorVersion { get; private set; }
    public Machine Machine { get; private set; }
    public string? OwnerComposite { get; private set; }
    /// <summary>Problems met while reading the entry point tables (the rest of the image stays usable).</summary>
    public List<string> Warnings { get; } = new();

    public string Architecture => ArchitectureName(Machine);

    public static string ArchitectureName(Machine m) => m switch
    {
        Machine.Amd64 => "x64",
        Machine.I386 => "x86",
        Machine.Arm64 => "arm64",
        Machine.Arm or Machine.ArmThumb2 or Machine.Thumb => "arm",
        _ => m.ToString().ToLowerInvariant(),
    };

    public static Machine NormalizeMachine(Machine machine)
    {
        foreach (var x in OsXor)
        {
            var m = (Machine)((ushort)machine ^ x);
            if (m is Machine.Amd64 or Machine.I386 or Machine.Arm64 or Machine.Arm or Machine.ArmThumb2 or Machine.Thumb) return m;
        }
        return machine;
    }

    public static R2RReader? TryOpen(LoadedAssembly asm, out string? error)
    {
        error = null;
        var dir = asm.PE.PEHeaders.CorHeader!.ManagedNativeHeaderDirectory;
        var header = asm.GetRvaSpan(dir.RelativeVirtualAddress);
        if (header.Length < 16 || BinaryPrimitives.ReadUInt32LittleEndian(header) != Signature)
        {
            error = "not a ReadyToRun image";
            return null;
        }
        var r = new R2RReader(asm)
        {
            MajorVersion = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(4)),
            MinorVersion = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(6)),
            Machine = NormalizeMachine(asm.PE.PEHeaders.CoffHeader.Machine),
        };
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(8));
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(12));
        for (int i = 0; i < count && 16 + i * 12 + 12 <= header.Length; i++)
        {
            var s = header.Slice(16 + i * 12);
            r._sections[(int)BinaryPrimitives.ReadUInt32LittleEndian(s)] =
                ((int)BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(4)), (int)BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(8)));
        }
        if ((flags & FlagComponent) != 0 || r._sections.ContainsKey(SectionOwnerCompositeExecutable))
        {
            if (r._sections.TryGetValue(SectionOwnerCompositeExecutable, out var owner))
            {
                var name = asm.GetRvaSpan(owner.Rva);
                int len = name.IndexOf((byte)0);
                r.OwnerComposite = System.Text.Encoding.UTF8.GetString(len < 0 ? name.Slice(0, Math.Min(owner.Size, name.Length)) : name.Slice(0, len));
            }
            else r.OwnerComposite = "?";
        }
        if (r._sections.TryGetValue(SectionRuntimeFunctions, out var functions))
            r._functions = new RuntimeFunctionTable(asm, r.Machine, functions.Rva, functions.Size);
        return r;
    }

    /// <summary>Generic instantiations precompiled into this image (methods of this module only).</summary>
    public IReadOnlyList<R2RInstance> Instances
    {
        get
        {
            EnsureIndex();
            return _instances!;
        }
    }

    /// <summary>Native code of a non-generic method definition.</summary>
    public bool TryGetMethodDef(int methodRid, out List<R2RFunction> bodies, out string? reason)
    {
        bodies = new List<R2RFunction>();
        if (!CheckUsable(out reason)) return false;
        EnsureIndex();
        if (!_methodDefEntries!.TryGetValue(methodRid, out int index))
        {
            reason = "method was not precompiled into the ReadyToRun image";
            return false;
        }
        bodies = GetBodies(index);
        if (bodies.Count == 0)
        {
            reason = "cannot determine native code size";
            return false;
        }
        return true;
    }

    public bool CheckUsable(out string? reason)
    {
        reason = null;
        if (OwnerComposite != null)
        {
            reason = $"native code is in the composite image '{OwnerComposite}' (not supported)";
            return false;
        }
        if (_functions == null || !_sections.ContainsKey(SectionMethodDefEntryPoints))
        {
            reason = "image has no method entry points";
            return false;
        }
        return true;
    }

    /// <summary>
    /// The main body and the funclets of a method. Funclets are the RUNTIME_FUNCTION entries that follow
    /// the entry point up to the next method entry point (the layout used by the runtime and R2RDump).
    /// </summary>
    public List<R2RFunction> GetBodies(int functionIndex)
    {
        EnsureIndex();
        var list = new List<R2RFunction>();
        var table = _functions!;
        int i = functionIndex;
        do
        {
            int size = table.Size(i);
            if (size > 0) list.Add(new R2RFunction(i == functionIndex ? "main" : $"funclet {i - functionIndex}", table.Begin(i), size));
            i++;
        }
        while (i < table.Count && !_isEntryPoint![i]);
        return list;
    }

    public byte[] ReadCode(int rva, int size)
    {
        var span = _asm.GetRvaSpan(rva);
        return span.Slice(0, Math.Min(size, span.Length)).ToArray();
    }

    // ---- entry point index

    private void EnsureIndex()
    {
        if (_isEntryPoint != null) return;
        lock (_indexLock)
        {
            if (_isEntryPoint != null) return;
            var isEntry = new bool[_functions?.Count ?? 0];
            var defs = new Dictionary<int, int>();
            var instances = new List<R2RInstance>();
            try
            {
                if (_functions != null && _sections.TryGetValue(SectionMethodDefEntryPoints, out var defSection))
                    ReadMethodDefEntries(_asm.GetRvaSpan(defSection.Rva), defs, isEntry);
            }
            catch (Exception ex) when (ex is BadImageFormatException or IndexOutOfRangeException or ArgumentOutOfRangeException)
            {
                Warnings.Add("cannot read MethodDefEntryPoints: " + ex.Message);
            }
            try
            {
                if (_functions != null && _sections.TryGetValue(SectionInstanceMethodEntryPoints, out var instSection))
                    ReadInstanceEntries(_asm.GetRvaSpan(instSection.Rva), instSection.Size, instances, isEntry);
            }
            catch (Exception ex) when (ex is BadImageFormatException or IndexOutOfRangeException or ArgumentOutOfRangeException)
            {
                Warnings.Add("cannot read InstanceMethodEntryPoints: " + ex.Message);
            }
            _methodDefEntries = defs;
            _instances = instances;
            _isEntryPoint = isEntry;
        }
    }

    private static void ReadMethodDefEntries(ReadOnlySpan<byte> data, Dictionary<int, int> defs, bool[] isEntry)
    {
        DecodeUnsigned(data, 0, out uint header);
        uint count = header >> 2;
        for (uint i = 0; i < count; i++)
        {
            if (!NativeArrayTryGet(data, i, out int offset)) continue;
            int index = DecodeEntryPoint(data, offset);
            if (index < 0 || index >= isEntry.Length) continue;
            defs[(int)i + 1] = index;
            isEntry[index] = true;
        }
    }

    private void ReadInstanceEntries(ReadOnlySpan<byte> data, int sectionSize, List<R2RInstance> instances, bool[] isEntry)
    {
        // NativeHashtable: header byte, then a bucket table; each bucket holds (low hash byte, relative offset) pairs.
        byte header = data[0];
        const int baseOffset = 1;
        int bucketCount = 1 << (header >> 2);
        int entryIndexSize = header & 3;
        if (entryIndexSize > 2) throw new BadImageFormatException("invalid hashtable header");

        for (int bucket = 0; bucket < bucketCount; bucket++)
        {
            uint start, end;
            switch (entryIndexSize)
            {
                case 0:
                    start = data[baseOffset + bucket];
                    end = data[baseOffset + bucket + 1];
                    break;
                case 1:
                    start = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(baseOffset + 2 * bucket));
                    end = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(baseOffset + 2 * bucket + 2));
                    break;
                default:
                    start = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(baseOffset + 4 * bucket));
                    end = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(baseOffset + 4 * bucket + 4));
                    break;
            }
            uint pos = baseOffset + start;
            uint stop = Math.Min(baseOffset + end, (uint)sectionSize);
            while (pos < stop)
            {
                pos++; // low byte of the hash code
                uint deltaPos = pos;
                pos = DecodeSigned(data, pos, out int delta);
                int entryOffset = (int)(deltaPos + delta);
                var instance = ParseInstance(data, entryOffset);
                if (instance.FunctionIndex >= 0 && instance.FunctionIndex < isEntry.Length)
                {
                    isEntry[instance.FunctionIndex] = true;
                    if (!instance.Method.IsNil) instances.Add(instance);
                }
            }
        }
    }

    // ReadyToRunMethodSigFlags
    private const uint SigUnboxingStub = 0x01, SigInstantiatingStub = 0x02, SigMethodInstantiation = 0x04, SigSlotInsteadOfToken = 0x08,
        SigMemberRefToken = 0x10, SigConstrained = 0x20, SigOwnerType = 0x40, SigUpdateContext = 0x80;

    private R2RInstance ParseInstance(ReadOnlySpan<byte> data, int offset)
    {
        var sig = new R2RSignatureReader(data, offset, _asm.Metadata);
        uint flags = sig.ReadUInt();
        bool foreign = false;
        if ((flags & SigUpdateContext) != 0)
        {
            sig.ReadUInt(); // module index: the method belongs to another module of the version bubble
            foreign = true;
            sig.Foreign = true;
        }
        string? owner = (flags & SigOwnerType) != 0 ? sig.ReadType() : null;
        if ((flags & SigSlotInsteadOfToken) != 0) throw new BadImageFormatException("slot-based method signatures are not supported");
        uint rid = sig.ReadUInt();
        EntityHandle method = foreign ? default
            : (flags & SigMemberRefToken) != 0 ? MetadataTokens.MemberReferenceHandle((int)rid)
            : MetadataTokens.MethodDefinitionHandle((int)rid);
        string[] methodArgs = Array.Empty<string>();
        if ((flags & SigMethodInstantiation) != 0)
        {
            methodArgs = new string[sig.ReadUInt()];
            for (int i = 0; i < methodArgs.Length; i++) methodArgs[i] = sig.ReadType();
        }
        if ((flags & SigConstrained) != 0) sig.ReadType();
        int functionIndex = DecodeEntryPoint(data, sig.Offset);

        string name = method.IsNil ? $"<foreign 0x{rid:X}>" : MethodName(method);
        var label = (owner ?? "") + "::" + name + (methodArgs.Length > 0 ? "<" + string.Join(", ", methodArgs) + ">" : "");
        if ((flags & SigUnboxingStub) != 0) label += " (unboxing stub)";
        if ((flags & SigInstantiatingStub) != 0) label += " (instantiating stub)";
        return new R2RInstance
        {
            Method = method,
            Label = label.TrimStart(':'),
            FunctionIndex = functionIndex,
            IsUnboxingStub = (flags & SigUnboxingStub) != 0,
            IsInstantiatingStub = (flags & SigInstantiatingStub) != 0,
        };
    }

    private string MethodName(EntityHandle method)
    {
        var md = _asm.Metadata;
        try
        {
            return method.Kind == HandleKind.MethodDefinition
                ? md.GetString(md.GetMethodDefinition((MethodDefinitionHandle)method).Name)
                : md.GetString(md.GetMemberReference((MemberReferenceHandle)method).Name);
        }
        catch (Exception)
        {
            return "?";
        }
    }

    /// <summary>Entry point record: runtime function index, optionally preceded by fixups.</summary>
    private static int DecodeEntryPoint(ReadOnlySpan<byte> data, int offset)
    {
        DecodeUnsigned(data, (uint)offset, out uint id);
        return (int)((id & 1) != 0 ? id >> 2 : id >> 1);
    }

    // ---- NativeFormat (see ILCompiler.Reflection.ReadyToRun NativeArray / NativeReader)

    private const int BlockSize = 16;

    private static bool NativeArrayTryGet(ReadOnlySpan<byte> image, uint index, out int elementOffset)
    {
        elementOffset = 0;
        uint baseOffset = DecodeUnsigned(image, 0, out uint header);
        uint count = header >> 2;
        int entryIndexSize = (int)(header & 3);
        if (index >= count) return false;

        uint offset = entryIndexSize switch
        {
            0 => image[(int)(baseOffset + index / BlockSize)],
            1 => BinaryPrimitives.ReadUInt16LittleEndian(image.Slice((int)(baseOffset + 2 * (index / BlockSize)))),
            _ => BinaryPrimitives.ReadUInt32LittleEndian(image.Slice((int)(baseOffset + 4 * (index / BlockSize)))),
        };
        offset += baseOffset;

        for (uint bit = BlockSize >> 1; bit > 0; bit >>= 1)
        {
            uint next = DecodeUnsigned(image, offset, out uint val);
            if ((index & bit) != 0)
            {
                if ((val & 2) != 0)
                {
                    offset += val >> 2;
                    continue;
                }
            }
            else if ((val & 1) != 0)
            {
                offset = next;
                continue;
            }
            // Not found, unless this is a matching special leaf node.
            if ((val & 3) == 0 && (val >> 2) == (index & (BlockSize - 1)))
            {
                offset = next;
                break;
            }
            return false;
        }
        elementOffset = (int)offset;
        return true;
    }

    /// <summary>Variable-length unsigned integer; returns the offset after it.</summary>
    internal static uint DecodeUnsigned(ReadOnlySpan<byte> image, uint offset, out uint value)
    {
        int o = (int)offset;
        uint b = image[o];
        if ((b & 1) == 0) { value = b >> 1; return offset + 1; }
        if ((b & 2) == 0) { value = (b >> 2) | ((uint)image[o + 1] << 6); return offset + 2; }
        if ((b & 4) == 0) { value = (b >> 3) | ((uint)image[o + 1] << 5) | ((uint)image[o + 2] << 13); return offset + 3; }
        if ((b & 8) == 0) { value = (b >> 4) | ((uint)image[o + 1] << 4) | ((uint)image[o + 2] << 12) | ((uint)image[o + 3] << 20); return offset + 4; }
        if ((b & 16) == 0) { value = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(o + 1)); return offset + 5; }
        throw new BadImageFormatException("invalid NativeFormat integer");
    }

    /// <summary>Variable-length signed integer (the highest byte is sign-extended).</summary>
    internal static uint DecodeSigned(ReadOnlySpan<byte> image, uint offset, out int value)
    {
        int o = (int)offset;
        int b = image[o];
        if ((b & 1) == 0) { value = (sbyte)image[o] >> 1; return offset + 1; }
        if ((b & 2) == 0) { value = (b >> 2) | ((sbyte)image[o + 1] << 6); return offset + 2; }
        if ((b & 4) == 0) { value = (b >> 3) | (image[o + 1] << 5) | ((sbyte)image[o + 2] << 13); return offset + 3; }
        if ((b & 8) == 0) { value = (b >> 4) | (image[o + 1] << 4) | (image[o + 2] << 12) | ((sbyte)image[o + 3] << 20); return offset + 4; }
        if ((b & 16) == 0) { value = BinaryPrimitives.ReadInt32LittleEndian(image.Slice(o + 1)); return offset + 5; }
        throw new BadImageFormatException("invalid NativeFormat integer");
    }
}

/// <summary>
/// Reads R2R type signatures: ECMA-335 signature encoding plus the ZapSig extensions
/// (__Canon, types from another module of the version bubble). Produces C#-like short names.
/// </summary>
internal ref struct R2RSignatureReader
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly MetadataReader _md;

    public R2RSignatureReader(ReadOnlySpan<byte> data, int offset, MetadataReader md)
    {
        _data = data;
        Offset = offset;
        _md = md;
        Foreign = false;
    }

    public int Offset { get; private set; }
    /// <summary>Tokens refer to another module whose metadata is not available.</summary>
    public bool Foreign { get; set; }

    /// <summary>ECMA-335 compressed unsigned integer.</summary>
    public uint ReadUInt()
    {
        byte b = _data[Offset];
        if ((b & 0x80) == 0)
        {
            Offset += 1;
            return b;
        }
        if ((b & 0xC0) == 0x80)
        {
            uint v = (uint)((b & 0x3F) << 8 | _data[Offset + 1]);
            Offset += 2;
            return v;
        }
        uint w = (uint)((b & 0x1F) << 24 | _data[Offset + 1] << 16 | _data[Offset + 2] << 8 | _data[Offset + 3]);
        Offset += 4;
        return w;
    }

    public string ReadType()
    {
        byte et = _data[Offset++];
        switch (et)
        {
            case 0x01: return "void";
            case 0x02: return "bool";
            case 0x03: return "char";
            case 0x04: return "sbyte";
            case 0x05: return "byte";
            case 0x06: return "short";
            case 0x07: return "ushort";
            case 0x08: return "int";
            case 0x09: return "uint";
            case 0x0A: return "long";
            case 0x0B: return "ulong";
            case 0x0C: return "float";
            case 0x0D: return "double";
            case 0x0E: return "string";
            case 0x16: return "TypedReference";
            case 0x18: return "nint";
            case 0x19: return "nuint";
            case 0x1C: return "object";
            case 0x0F: return ReadType() + "*";
            case 0x10: return "ref " + ReadType();
            case 0x11:
            case 0x12: return TypeToken(ReadUInt());
            case 0x13: return "!" + ReadUInt();
            case 0x1E: return "!!" + ReadUInt();
            case 0x1D: return ReadType() + "[]";
            case 0x14:
            {
                var element = ReadType();
                uint rank = ReadUInt();
                uint sizes = ReadUInt();
                for (int i = 0; i < sizes; i++) ReadUInt();
                uint bounds = ReadUInt();
                for (int i = 0; i < bounds; i++) ReadUInt();
                return element + "[" + new string(',', (int)Math.Max(0, rank - 1)) + "]";
            }
            case 0x15:
            {
                var generic = ReadType();
                var args = new string[ReadUInt()];
                for (int i = 0; i < args.Length; i++) args[i] = ReadType();
                return generic + "<" + string.Join(", ", args) + ">";
            }
            case 0x1B:
            {
                byte callConv = _data[Offset++];
                if ((callConv & 0x10) != 0) ReadUInt(); // generic parameter count
                var ps = new string[ReadUInt()];
                var ret = ReadType();
                for (int i = 0; i < ps.Length; i++) ps[i] = ReadType();
                return "delegate*<" + string.Join(", ", ps.Append(ret)) + ">";
            }
            case 0x1F:
            case 0x20:
                ReadUInt(); // custom modifier token
                return ReadType();
            case 0x41: // sentinel
            case 0x45: // pinned
            case 0x3D: // ELEMENT_TYPE_NATIVE_VALUETYPE_ZAPSIG
                return ReadType();
            case 0x3E: // ELEMENT_TYPE_CANON_ZAPSIG
                return "__Canon";
            case 0x3F: // ELEMENT_TYPE_MODULE_ZAPSIG: the type comes from another module
            {
                uint module = ReadUInt();
                bool saved = Foreign;
                Foreign = true;
                var t = ReadType();
                Foreign = saved;
                return t.StartsWith('<') ? $"<module {module}>{t}" : t;
            }
            default:
                throw new BadImageFormatException($"unsupported element type 0x{et:X2} in R2R signature");
        }
    }

    private string TypeToken(uint coded)
    {
        int rid = (int)(coded >> 2);
        int tag = (int)(coded & 3);
        if (Foreign) return $"<type 0x{(tag switch { 0 => 0x02, 1 => 0x01, _ => 0x1B }) << 24 | rid:X8}>";
        try
        {
            switch (tag)
            {
                case 0:
                    return Short(Metadata.Names.TypeDefFullName(_md, MetadataTokens.TypeDefinitionHandle(rid)));
                case 1:
                    return Short(Metadata.Names.TypeRefFullName(_md, MetadataTokens.TypeReferenceHandle(rid)));
                default:
                    return _md.GetTypeSpecification(MetadataTokens.TypeSpecificationHandle(rid)).DecodeSignature(Metadata.CSharpTypeProvider.Brief, Metadata.GenericContext.Empty);
            }
        }
        catch (Exception)
        {
            return $"<type rid {rid}>";
        }
    }

    private static string Short(string fullName) => Metadata.Names.StripArity(Metadata.Names.ShortName(fullName));
}
