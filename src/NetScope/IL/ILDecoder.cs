using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace NetScope.IL;

public sealed class ILInstruction
{
    public int Offset { get; init; }
    public int Size { get; init; }
    public OpCode OpCode { get; init; }

    /// <summary>
    /// Raw operand: int (token, variable index, int32 constant, branch target), long, float, double,
    /// or int[] (switch targets). Branch targets are absolute IL offsets.
    /// </summary>
    public object? Operand { get; init; }

    public int NextOffset => Offset + Size;
    public string Name => OpCode.Name ?? "?";
    public int Token => Operand is int i ? i : 0;
    public EntityHandle Handle => MetadataTokens.EntityHandle(Token);
    public int BranchTarget => (int)Operand!;
    public int[] SwitchTargets => (int[])Operand!;

    public bool IsBranch => OpCode.OperandType is OperandType.InlineBrTarget or OperandType.ShortInlineBrTarget;
    public bool IsSwitch => OpCode.OperandType == OperandType.InlineSwitch;
    public bool IsConditionalBranch => OpCode.FlowControl == FlowControl.Cond_Branch && !IsSwitch;
    public bool IsUnconditionalBranch => OpCode.FlowControl == FlowControl.Branch; // br, leave
    public bool IsLeave => OpCode.Value == OpCodes.Leave.Value || OpCode.Value == OpCodes.Leave_S.Value;

    /// <summary>ret, throw, rethrow, jmp: control leaves the method.</summary>
    public bool IsMethodExit =>
        OpCode.Value == OpCodes.Ret.Value || OpCode.Value == OpCodes.Throw.Value ||
        OpCode.Value == OpCodes.Rethrow.Value || OpCode.Value == OpCodes.Jmp.Value;

    /// <summary>endfinally/endfault/endfilter: control leaves a handler.</summary>
    public bool IsHandlerExit => OpCode.Value == OpCodes.Endfinally.Value || OpCode.Value == OpCodes.Endfilter.Value;

    /// <summary>Instruction after which the next instruction starts a new basic block.</summary>
    public bool EndsBlock => IsBranch || IsSwitch || IsMethodExit || IsHandlerExit;

    public bool IsPrefix => OpCode.OpCodeType == OpCodeType.Prefix;
}

public static class ILDecoder
{
    private static readonly OpCode?[] OneByte = new OpCode?[256];
    private static readonly OpCode?[] TwoByte = new OpCode?[256];

    static ILDecoder()
    {
        foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (f.GetValue(null) is not OpCode op) continue;
            var value = (ushort)op.Value;
            if (op.Size == 1) OneByte[value] = op;
            else TwoByte[value & 0xFF] = op;
        }
    }

    /// <summary>Decodes the IL byte stream. Throws <see cref="ILDecodeException"/> with the offset on malformed IL.</summary>
    public static List<ILInstruction> Decode(BlobReader reader)
    {
        var list = new List<ILInstruction>(reader.Length / 2 + 1);
        while (reader.RemainingBytes > 0)
        {
            int offset = reader.Offset;
            try
            {
                list.Add(DecodeOne(ref reader, offset));
            }
            catch (BadImageFormatException ex)
            {
                throw new ILDecodeException(offset, "truncated instruction: " + ex.Message);
            }
        }
        return list;
    }

    private static ILInstruction DecodeOne(ref BlobReader reader, int offset)
    {
        byte b = reader.ReadByte();
        OpCode? opc;
        if (b == 0xFE)
        {
            if (reader.RemainingBytes == 0) throw new ILDecodeException(offset, "truncated two-byte opcode");
            byte b2 = reader.ReadByte();
            opc = TwoByte[b2];
            if (opc == null) throw new ILDecodeException(offset, $"unknown opcode 0xFE{b2:X2}");
        }
        else
        {
            opc = OneByte[b];
            if (opc == null) throw new ILDecodeException(offset, $"unknown opcode 0x{b:X2}");
        }
        var op = opc.Value;

        object? operand = null;
        switch (op.OperandType)
        {
            case OperandType.InlineNone:
                break;
            case OperandType.ShortInlineBrTarget:
            {
                sbyte delta = reader.ReadSByte();
                operand = reader.Offset + delta;
                break;
            }
            case OperandType.InlineBrTarget:
            {
                int delta = reader.ReadInt32();
                operand = reader.Offset + delta;
                break;
            }
            case OperandType.ShortInlineI:
                operand = op.Value == OpCodes.Ldc_I4_S.Value ? reader.ReadSByte() : (int)reader.ReadByte();
                break;
            case OperandType.ShortInlineVar:
                operand = (int)reader.ReadByte();
                break;
            case OperandType.InlineVar:
                operand = (int)reader.ReadUInt16();
                break;
            case OperandType.InlineI:
            case OperandType.InlineMethod:
            case OperandType.InlineField:
            case OperandType.InlineType:
            case OperandType.InlineTok:
            case OperandType.InlineSig:
            case OperandType.InlineString:
                operand = reader.ReadInt32();
                break;
            case OperandType.InlineI8:
                operand = reader.ReadInt64();
                break;
            case OperandType.ShortInlineR:
                operand = reader.ReadSingle();
                break;
            case OperandType.InlineR:
                operand = reader.ReadDouble();
                break;
            case OperandType.InlineSwitch:
            {
                uint count = reader.ReadUInt32();
                if (count > (uint)reader.RemainingBytes / 4) throw new ILDecodeException(offset, "switch table exceeds method body");
                var deltas = new int[count];
                for (int i = 0; i < count; i++) deltas[i] = reader.ReadInt32();
                int baseOffset = reader.Offset;
                var targets = new int[count];
                for (int i = 0; i < count; i++) targets[i] = baseOffset + deltas[i];
                operand = targets;
                break;
            }
            default:
                throw new ILDecodeException(offset, "unsupported operand type " + op.OperandType);
        }
        return new ILInstruction { Offset = offset, Size = reader.Offset - offset, OpCode = op, Operand = operand };
    }
}

public sealed class ILDecodeException : Exception
{
    public ILDecodeException(int offset, string message) : base(message) => Offset = offset;

    public int Offset { get; }
}
