using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;

namespace NetScope.Native;

/// <summary>A compiled method of a NativeAOT image named by the image's own stack trace metadata.</summary>
public sealed record NativeAotMethod(int Rva, string Name, bool HiddenFromStackTraces);

/// <summary>
/// Reads method names of a NativeAOT PE image without a PDB (TZ-NetScope-3 §4.3). NativeAOT keeps the data that
/// lets Exception.StackTrace name frames: the blob "StackTraceMethodRvaToTokenMapping" (a method entry point and
/// a reference to its name and owning type for each compiled method) and "EmbeddedMetadata" (the names, in the
/// NativeFormat metadata encoding). Both are found through the ReadyToRun-style module header ('RTR') that ILC
/// emits. Only the file's bytes are read: nothing is loaded or executed.
///
/// The data is present unless the application was published with StackTraceSupport=false; then there are no
/// names to recover and <see cref="TryRead"/> says so. Layouts follow dotnet/runtime (release/8.0 through
/// release/10.0): the handle encoding changed (type in the top 8 bits in .NET 8, top 7 bits later) and is
/// detected from the data.
/// </summary>
public sealed class NativeAotReader
{
    private const uint HeaderSignature = 0x00525452;            // 'RTR'
    private const int ReadonlyBlobRegionStart = 300;            // ReadyToRunSectionType
    private const int BlobEmbeddedMetadata = 13;                // ReflectionMapBlob.EmbeddedMetadata
    private const int BlobStackTraceRvaToToken = 27;            // ReflectionMapBlob.BlobIdStackTraceMethodRvaToTokenMapping
    private const uint MetadataSignature = 0xDEADDFFD;

    // StackTraceDataCommand
    private const byte UpdateOwningType = 0x01, UpdateName = 0x02, UpdateSignature = 0x04, UpdateGenericSignature = 0x08, IsStackTraceHidden = 0x10;

    // HandleType values (identical in .NET 8, 9 and 10).
    private const int HtConstantStringValue = 0x1a, HtNamespaceDefinition = 0x2f, HtNamespaceReference = 0x30,
        HtTypeDefinition = 0x3a, HtTypeInstantiationSignature = 0x3c, HtTypeReference = 0x3d, HtTypeSpecification = 0x3e;

    private readonly byte[] _file;
    private readonly PEHeaders _headers;
    private byte[] _metadata = Array.Empty<byte>();
    private int _handleShift;                                   // 24 (.NET 8) or 25 (.NET 9+)
    private readonly Dictionary<uint, string> _typeNames = new();

    private NativeAotReader(byte[] file, PEHeaders headers)
    {
        _file = file;
        _headers = headers;
    }

    public ushort MajorVersion { get; private set; }
    public ushort MinorVersion { get; private set; }
    public List<NativeAotMethod> Methods { get; } = new();

    /// <summary>Reads the method names; returns null with the reason when the image has none to offer.</summary>
    public static NativeAotReader? TryRead(byte[] file, PEHeaders headers, out string? reason)
    {
        var r = new NativeAotReader(file, headers);
        try
        {
            reason = r.Read();
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or ArgumentException or InvalidDataException or OverflowException)
        {
            reason = "the NativeAOT data could not be decoded: " + ex.Message;
        }
        return reason == null ? r : null;
    }

    private string? Read()
    {
        var blobs = FindModuleHeader(out var headerError);
        if (blobs == null) return headerError;
        if (!blobs.TryGetValue(ReadonlyBlobRegionStart + BlobStackTraceRvaToToken, out var map))
            return "the image has no stack trace metadata (published with StackTraceSupport=false), so the method names cannot be recovered without a PDB";
        if (!blobs.TryGetValue(ReadonlyBlobRegionStart + BlobEmbeddedMetadata, out var md))
            return "the image has stack trace data but no embedded metadata";

        _metadata = RvaBytes(md.Rva, md.Size);
        if (_metadata.Length < 4 || BinaryPrimitives.ReadUInt32LittleEndian(_metadata) != MetadataSignature)
            return "the embedded metadata has an unknown format";
        ReadMap(map.Rva, RvaBytes(map.Rva, map.Size));
        Methods.Sort((a, b) => a.Rva.CompareTo(b.Rva));
        return Methods.Count == 0 ? "the stack trace metadata lists no methods" : null;
    }

    // ---------------------------------------------------------------- module header

    /// <summary>The reason given when the file has no NativeAOT module header at all (an ordinary native PE).</summary>
    public const string NoModuleHeader = "no NativeAOT module header ('RTR') was found";

    private Dictionary<int, (int Rva, int Size)>? FindModuleHeader(out string? error)
    {
        error = NoModuleHeader;
        var pe = _headers.PEHeader!;
        ulong imageBase = pe.ImageBase;
        int pointerSize = pe.Magic == PEMagic.PE32Plus ? 8 : 4;
        foreach (var section in _headers.SectionHeaders)
        {
            if ((section.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0) continue;
            int start = section.PointerToRawData, length = Math.Min(section.SizeOfRawData, _file.Length - start);
            for (int i = 0; i + 16 <= length; i += 4)
            {
                int at = start + i;
                if (BinaryPrimitives.ReadUInt32LittleEndian(_file.AsSpan(at)) != HeaderSignature) continue;
                ushort major = BinaryPrimitives.ReadUInt16LittleEndian(_file.AsSpan(at + 4));
                ushort minor = BinaryPrimitives.ReadUInt16LittleEndian(_file.AsSpan(at + 6));
                ushort count = BinaryPrimitives.ReadUInt16LittleEndian(_file.AsSpan(at + 12));
                byte entrySize = _file[at + 14], entryType = _file[at + 15];
                if (entryType != 1 || entrySize != 8 + 2 * pointerSize || count == 0 || count > 1000) continue;
                if (at + 16 + count * entrySize > _file.Length) continue;

                var result = new Dictionary<int, (int, int)>();
                bool valid = true;
                for (int e = 0; e < count && valid; e++)
                {
                    int p = at + 16 + e * entrySize;
                    int id = BinaryPrimitives.ReadInt32LittleEndian(_file.AsSpan(p));
                    ulong startVa = pointerSize == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(_file.AsSpan(p + 8)) : BinaryPrimitives.ReadUInt32LittleEndian(_file.AsSpan(p + 8));
                    ulong endVa = pointerSize == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(_file.AsSpan(p + 8 + pointerSize)) : BinaryPrimitives.ReadUInt32LittleEndian(_file.AsSpan(p + 8 + pointerSize));
                    if (startVa < imageBase || startVa - imageBase >= (ulong)pe.SizeOfImage) { valid = false; break; }
                    int rva = (int)(startVa - imageBase);
                    int size = endVa > startVa && endVa - imageBase <= (ulong)pe.SizeOfImage ? (int)(endVa - startVa) : 0;
                    result[id] = (rva, size);
                }
                if (!valid) continue;
                MajorVersion = major;
                MinorVersion = minor;
                error = null;
                return result;
            }
        }
        return null;
    }

    private byte[] RvaBytes(int rva, int size)
    {
        foreach (var s in _headers.SectionHeaders)
        {
            int extent = Math.Max(s.VirtualSize, s.SizeOfRawData);
            if (rva < s.VirtualAddress || rva >= s.VirtualAddress + extent) continue;
            int offset = s.PointerToRawData + (rva - s.VirtualAddress);
            int available = Math.Max(0, Math.Min(size, Math.Min(s.SizeOfRawData - (rva - s.VirtualAddress), _file.Length - offset)));
            var bytes = new byte[size];
            Array.Copy(_file, offset, bytes, 0, available);
            return bytes;
        }
        throw new InvalidDataException($"RVA 0x{rva:X} is outside the image");
    }

    // ---------------------------------------------------------------- RVA -> name map

    private void ReadMap(int mapRva, byte[] map)
    {
        int count = BinaryPrimitives.ReadInt32LittleEndian(map);
        int pos = 4;
        uint owningType = 0, name = 0;
        for (int n = 0; n < count && pos < map.Length; n++)
        {
            byte command = map[pos++];
            if ((command & UpdateOwningType) != 0) { owningType = BinaryPrimitives.ReadUInt32LittleEndian(map.AsSpan(pos)); pos += 4; }
            if ((command & UpdateName) != 0) name = DecodeUnsigned(map, ref pos);
            if ((command & UpdateSignature) != 0) DecodeUnsigned(map, ref pos);
            if ((command & UpdateGenericSignature) != 0) { DecodeUnsigned(map, ref pos); DecodeUnsigned(map, ref pos); }
            int relative = BinaryPrimitives.ReadInt32LittleEndian(map.AsSpan(pos));
            int methodRva = mapRva + pos + relative;                    // a relative pointer to the entry point
            pos += 4;

            if (_handleShift == 0) _handleShift = DetectHandleShift(owningType);
            string type = TypeName(owningType);
            string method = name == 0 ? "?" : String(name);
            Methods.Add(new NativeAotMethod(methodRva, type.Length == 0 ? method : type + "::" + method, (command & IsStackTraceHidden) != 0));
        }
    }

    /// <summary>The owning type is written as a full handle token: its type tells the encoding apart.</summary>
    private static int DetectHandleShift(uint token)
    {
        if (token == 0) return 0;
        foreach (int shift in new[] { 25, 24 })
            if ((token >> shift) is HtTypeDefinition or HtTypeReference or HtTypeSpecification)
                return shift;
        throw new InvalidDataException($"unknown owning type token 0x{token:X8}");
    }

    // ---------------------------------------------------------------- NativeFormat metadata (subset)

    private (int Type, uint Offset) Split(uint token) =>
        _handleShift == 0 ? (0, 0) : ((int)(token >> _handleShift), token & ((1u << _handleShift) - 1));

    /// <summary>An untyped handle inside a record: (offset &lt;&lt; 8 | type) in .NET 8, (offset &lt;&lt; 7 | type) later.</summary>
    private (int Type, uint Offset) ReadHandle(ref int pos)
    {
        uint raw = DecodeUnsigned(_metadata, ref pos);
        int typeBits = _handleShift == 24 ? 8 : 7;
        return ((int)(raw & ((1u << typeBits) - 1)), raw >> typeBits);
    }

    private string TypeName(uint token)
    {
        if (token == 0) return "";
        if (_typeNames.TryGetValue(token, out var cached)) return cached;
        var (type, offset) = Split(token);
        string name = TypeName(type, offset, depth: 0);
        _typeNames[token] = name;
        return name;
    }

    private string TypeName(int type, uint offset, int depth)
    {
        if (offset == 0 || depth > 32) return "";
        int pos = (int)offset;
        switch (type)
        {
            case HtTypeDefinition:
            {
                DecodeUnsigned(_metadata, ref pos);                     // flags
                ReadHandle(ref pos);                                    // base type
                uint ns = DecodeUnsigned(_metadata, ref pos);           // NamespaceDefinitionHandle
                uint name = DecodeUnsigned(_metadata, ref pos);         // ConstantStringValueHandle
                DecodeUnsigned(_metadata, ref pos);                     // size
                DecodeUnsigned(_metadata, ref pos);                     // packing size
                uint enclosing = DecodeUnsigned(_metadata, ref pos);    // TypeDefinitionHandle
                string simple = String(name);
                if (enclosing != 0) return TypeName(HtTypeDefinition, enclosing, depth + 1) + "+" + simple;
                return Qualify(NamespaceName(HtNamespaceDefinition, ns, depth + 1), simple);
            }
            case HtTypeReference:
            {
                var parent = ReadHandle(ref pos);                       // namespace reference or enclosing type
                uint name = DecodeUnsigned(_metadata, ref pos);
                string simple = String(name);
                if (parent.Type is HtTypeReference or HtTypeDefinition)
                    return TypeName(parent.Type, parent.Offset, depth + 1) + "+" + simple;
                return Qualify(NamespaceName(parent.Type, parent.Offset, depth + 1), simple);
            }
            case HtTypeSpecification:
            {
                var signature = ReadHandle(ref pos);
                if (signature.Type != HtTypeInstantiationSignature) return "";
                int sp = (int)signature.Offset;
                var generic = ReadHandle(ref sp);                       // the generic type definition or reference
                return TypeName(generic.Type, generic.Offset, depth + 1);
            }
            default:
                return "";
        }
    }

    private string NamespaceName(int type, uint offset, int depth)
    {
        if (offset == 0 || depth > 64 || type is not (HtNamespaceDefinition or HtNamespaceReference)) return "";
        int pos = (int)offset;
        var parent = ReadHandle(ref pos);                               // scope or parent namespace
        uint name = DecodeUnsigned(_metadata, ref pos);
        string simple = name == 0 ? "" : String(name);
        string outer = parent.Type == type ? NamespaceName(parent.Type, parent.Offset, depth + 1) : "";
        return Qualify(outer, simple);
    }

    private static string Qualify(string ns, string name) => ns.Length == 0 ? name : name.Length == 0 ? ns : ns + "." + name;

    /// <summary>A ConstantStringValue record: a length, then UTF-8 bytes.</summary>
    private string String(uint offset)
    {
        if (offset == 0) return "";
        int pos = (int)offset;
        uint length = DecodeUnsigned(_metadata, ref pos);
        if (length > (uint)(_metadata.Length - pos)) throw new InvalidDataException("string out of range");
        return Encoding.UTF8.GetString(_metadata, pos, (int)length);
    }

    /// <summary>NativeFormat variable-length unsigned integer (NativePrimitiveDecoder.DecodeUnsigned).</summary>
    private static uint DecodeUnsigned(byte[] data, ref int pos)
    {
        uint val = data[pos];
        if ((val & 1) == 0) { pos += 1; return val >> 1; }
        if ((val & 2) == 0) { uint v = (val >> 2) | ((uint)data[pos + 1] << 6); pos += 2; return v; }
        if ((val & 4) == 0) { uint v = (val >> 3) | ((uint)data[pos + 1] << 5) | ((uint)data[pos + 2] << 13); pos += 3; return v; }
        if ((val & 8) == 0) { uint v = (val >> 4) | ((uint)data[pos + 1] << 4) | ((uint)data[pos + 2] << 12) | ((uint)data[pos + 3] << 20); pos += 4; return v; }
        if ((val & 16) == 0) { uint v = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 1)); pos += 5; return v; }
        throw new InvalidDataException("bad variable-length integer");
    }
}
