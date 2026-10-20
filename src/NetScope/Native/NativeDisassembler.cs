using Iced.Intel;

namespace NetScope.Native;

public sealed record NativeLine(ulong Address, string Bytes, string Text)
{
    /// <summary>Which part of the method the line belongs to (see <see cref="NativeCode.Parts"/>).</summary>
    public string? Part { get; init; }
}

/// <summary>A contiguous code range: method body, funclet, or one generic instantiation.</summary>
public sealed record NativePart(string Name, ulong Address, int Size);

public sealed class NativeCode
{
    public bool Available { get; init; }
    /// <summary>Why native code is not shown.</summary>
    public string? Reason { get; init; }
    /// <summary>R2R (read from the image) or JIT (compiled in a helper process).</summary>
    public string? Source { get; init; }
    public string? Architecture { get; init; }
    public string? RuntimeVersion { get; init; }
    /// <summary>Optimization level, e.g. "R2R precompiled" or "FullOpts".</summary>
    public string? Tier { get; init; }
    public ulong Address { get; init; }
    public int Size { get; init; }
    public List<NativeLine> Lines { get; init; } = new();
    /// <summary>Code ranges in order; a single "main" part for an ordinary method body.</summary>
    public List<NativePart> Parts { get; init; } = new();

    public static NativeCode Unavailable(string reason) => new() { Available = false, Reason = reason };

    /// <summary>Disassembles the given code ranges of an image into one result.</summary>
    public static NativeCode FromImage(string architecture, string source, string? runtime, string tier, IEnumerable<(string Name, int Rva, byte[] Code)> parts)
    {
        var lines = new List<NativeLine>();
        var list = new List<NativePart>();
        foreach (var (name, rva, code) in parts)
        {
            list.Add(new NativePart(name, (ulong)rva, code.Length));
            lines.AddRange(NativeDisassembler.Disassemble(architecture, code, (ulong)rva).Select(l => l with { Part = name }));
        }
        return new NativeCode
        {
            Available = true,
            Source = source,
            Architecture = architecture,
            RuntimeVersion = runtime,
            Tier = tier,
            Address = list.Count > 0 ? list[0].Address : 0,
            Size = list.Sum(p => p.Size),
            Lines = lines,
            Parts = list,
        };
    }
}

/// <summary>Disassembles x86/x64 with Iced and ARM64 with Disarm.</summary>
public static class NativeDisassembler
{
    public static List<NativeLine> Disassemble(string architecture, byte[] code, ulong address)
    {
        return architecture switch
        {
            "x64" => DisassembleX86(code, address, 64),
            "x86" => DisassembleX86(code, address, 32),
            "arm64" => DisassembleArm64(code, address),
            _ => Hex(code, address),
        };
    }

    /// <summary>
    /// Estimates the size of an x86/x64 function that has no unwind data (a leaf function or an import thunk):
    /// linear sweep up to a ret/jmp/int3 that is not followed by a branch target. Returns 0 if no end is found.
    /// </summary>
    public static int EstimateFunctionSize(ReadOnlySpan<byte> code, ulong address, int bitness)
    {
        var bytes = code.ToArray();
        var decoder = Decoder.Create(bitness, new ByteArrayCodeReader(bytes), address);
        ulong end = address + (ulong)bytes.Length;
        ulong furthestTarget = address;
        while (decoder.IP < end)
        {
            decoder.Decode(out var instr);
            if (instr.Code == Code.INVALID) return 0;
            if (instr.FlowControl is FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch &&
                instr.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64)
            {
                ulong target = instr.NearBranchTarget;
                if (target > instr.IP && target < end) furthestTarget = Math.Max(furthestTarget, target);
            }
            if (instr.FlowControl is FlowControl.Return or FlowControl.IndirectBranch or FlowControl.UnconditionalBranch or FlowControl.Interrupt &&
                instr.NextIP > furthestTarget)
            {
                return (int)(instr.NextIP - address);
            }
        }
        return 0;
    }

    private static List<NativeLine> DisassembleX86(byte[] code, ulong address, int bitness)
    {
        var lines = new List<NativeLine>();
        var reader = new ByteArrayCodeReader(code);
        var decoder = Decoder.Create(bitness, reader, address);
        var formatter = new IntelFormatter();
        formatter.Options.FirstOperandCharIndex = 8;
        formatter.Options.HexPrefix = "0x";
        formatter.Options.HexSuffix = "";
        formatter.Options.SpaceAfterOperandSeparator = true;
        formatter.Options.BranchLeadingZeros = false;
        formatter.Options.ShowBranchSize = false;
        var output = new StringOutput();
        ulong end = address + (ulong)code.Length;
        while (decoder.IP < end)
        {
            decoder.Decode(out var instr);
            formatter.Format(instr, output);
            int start = (int)(instr.IP - address);
            lines.Add(new NativeLine(instr.IP, Convert.ToHexString(code, start, instr.Length), output.ToStringAndReset()));
        }
        return lines;
    }

    private static List<NativeLine> DisassembleArm64(byte[] code, ulong address)
    {
        var lines = new List<NativeLine>();
        int count = code.Length / 4;
        var aligned = code.Length == count * 4 ? code : code.AsSpan(0, count * 4).ToArray();
        int i = 0;
        try
        {
            foreach (var ins in Disarm.Disassembler.Disassemble(aligned, address, Disarm.Disassembler.Options.IgnoreErrors))
            {
                var text = ins.ToString();
                // Disarm prefixes the text with the address: "0x00001000 STP ...".
                if (text.StartsWith("0x", StringComparison.Ordinal))
                {
                    int sp = text.IndexOf(' ');
                    if (sp > 0) text = text.Substring(sp + 1);
                }
                int offset = (int)(ins.Address - address);
                var bytes = offset >= 0 && offset + 4 <= aligned.Length ? Convert.ToHexString(aligned, offset, 4) : "";
                // Register 31 as a memory base is always SP (Disarm prints it as X31).
                lines.Add(new NativeLine(ins.Address, bytes, text.ToLowerInvariant().Replace("[x31", "[sp")));
                i++;
            }
        }
        catch (Exception ex)
        {
            lines.Add(new NativeLine(address + (ulong)(i * 4), "", "; disassembly stopped: " + ex.Message));
        }
        return lines;
    }

    private static List<NativeLine> Hex(byte[] code, ulong address)
    {
        var lines = new List<NativeLine>();
        for (int i = 0; i < code.Length; i += 16)
        {
            int n = Math.Min(16, code.Length - i);
            lines.Add(new NativeLine(address + (ulong)i, Convert.ToHexString(code, i, n), "; (no disassembler for this architecture)"));
        }
        return lines;
    }
}
