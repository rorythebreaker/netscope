using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using NetScope.Loading;

namespace NetScope.Native;

/// <summary>
/// A table of RUNTIME_FUNCTION entries: the R2R RuntimeFunctions section, or the PE exception
/// directory (.pdata) of a mixed-mode image. Gives the start and size of each native function.
/// </summary>
public sealed class RuntimeFunctionTable
{
    private readonly LoadedAssembly _asm;
    private readonly int _rva;
    private int[]? _sortedBegins;

    public RuntimeFunctionTable(LoadedAssembly asm, Machine machine, int rva, int size)
    {
        _asm = asm;
        Machine = machine;
        _rva = rva;
        // x64: BeginAddress, EndAddress, UnwindData. ARM64/ARM/x86 (R2R): BeginAddress, UnwindData.
        EntrySize = machine == Machine.Amd64 ? 12 : 8;
        Count = size / EntrySize;
    }

    public Machine Machine { get; }
    public int EntrySize { get; }
    public int Count { get; }

    /// <summary>The exception directory of a PE image, or null when there is none.</summary>
    public static RuntimeFunctionTable? FromPdata(LoadedAssembly asm)
    {
        var dir = asm.PE.PEHeaders.PEHeader?.ExceptionTableDirectory;
        if (dir == null || dir.Value.Size == 0 || dir.Value.RelativeVirtualAddress == 0) return null;
        var machine = R2RReader.NormalizeMachine(asm.PE.PEHeaders.CoffHeader.Machine);
        if (machine is not (Machine.Amd64 or Machine.Arm64)) return null;
        return new RuntimeFunctionTable(asm, machine, dir.Value.RelativeVirtualAddress, dir.Value.Size);
    }

    private ReadOnlySpan<byte> Entry(int index) => _asm.GetRvaSpan(_rva).Slice(index * EntrySize, EntrySize);

    public int Begin(int index)
    {
        int begin = (int)BinaryPrimitives.ReadUInt32LittleEndian(Entry(index));
        return Machine is Machine.Arm or Machine.ArmThumb2 or Machine.Thumb ? begin & ~1 : begin;
    }

    /// <summary>Code size of one function, or 0 when it cannot be determined.</summary>
    public int Size(int index)
    {
        var entry = Entry(index);
        int begin = Begin(index);
        int size = 0;
        if (Machine == Machine.Amd64)
        {
            size = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(4)) - begin;
        }
        else if (Machine == Machine.Arm64)
        {
            uint unwind = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(4));
            if ((unwind & 3) != 0) size = (int)((unwind >> 2) & 0x7FF) * 4; // packed unwind data
            else
            {
                var xdata = _asm.GetRvaSpan((int)unwind);
                if (xdata.Length >= 4) size = (int)(BinaryPrimitives.ReadUInt32LittleEndian(xdata) & 0x3FFFF) * 4;
            }
        }
        if (size <= 0 || size > 1 << 20)
        {
            // Unknown layout (x86, arm): up to the next function.
            var begins = SortedBegins();
            int next = Array.BinarySearch(begins, begin + 1);
            if (next < 0) next = ~next;
            size = next < begins.Length ? begins[next] - begin : 0;
        }
        return Math.Max(0, size);
    }

    /// <summary>Index of the function that starts exactly at an RVA, or -1.</summary>
    public int FindByBegin(int rva)
    {
        // The table is sorted by BeginAddress, as the OS unwinder requires.
        int lo = 0, hi = Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            int b = Begin(mid);
            if (b == rva) return mid;
            if (b < rva) lo = mid + 1;
            else hi = mid - 1;
        }
        return -1;
    }

    private int[] SortedBegins()
    {
        if (_sortedBegins != null) return _sortedBegins;
        var begins = new int[Count];
        for (int i = 0; i < Count; i++) begins[i] = Begin(i);
        Array.Sort(begins);
        return _sortedBegins = begins;
    }
}
