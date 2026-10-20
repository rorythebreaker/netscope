using NetScope.IL;

namespace NetScope.Cfg;

public enum EdgeKind
{
    /// <summary>Unconditional jump (br) or fall-through into a block that starts at a jump target.</summary>
    Jump,
    Fallthrough,
    /// <summary>Conditional branch taken.</summary>
    True,
    /// <summary>Conditional branch not taken (fall-through).</summary>
    False,
    Case,
    Default,
    Leave,
    /// <summary>From the start of a protected region to its handler.</summary>
    Exception,
}

public sealed class CfgEdge
{
    public required BasicBlock From { get; init; }
    public required BasicBlock To { get; init; }
    public EdgeKind Kind { get; init; }
    public int CaseIndex { get; init; }
    /// <summary>Condition under which the edge is followed (for True/False edges), or handler description.</summary>
    public string? Condition { get; set; }
    public bool IsBackEdge { get; set; }

    public string KindName => Kind switch
    {
        EdgeKind.Jump => "jump",
        EdgeKind.Fallthrough => "fallthrough",
        EdgeKind.True => "true",
        EdgeKind.False => "false",
        EdgeKind.Case => "case",
        EdgeKind.Default => "default",
        EdgeKind.Leave => "leave",
        _ => "exception",
    };

    /// <summary>Short label for graphs.</summary>
    public string Label => Kind switch
    {
        EdgeKind.True or EdgeKind.False => Condition ?? KindName,
        EdgeKind.Case => "case " + CaseIndex,
        EdgeKind.Default => "default",
        EdgeKind.Leave => "leave",
        EdgeKind.Exception => Condition ?? "exception",
        _ => "",
    };
}

public sealed class BasicBlock
{
    public int Index { get; init; }
    public int StartOffset { get; init; }
    /// <summary>Offset of the last byte of the block.</summary>
    public int EndOffset { get; init; }
    public required List<ILInstruction> Instructions { get; init; }
    public List<CfgEdge> Successors { get; } = new();
    public List<CfgEdge> Predecessors { get; } = new();
    public bool Reachable { get; set; }
    public bool IsLoopHeader { get; set; }
    /// <summary>return, throw, rethrow, jmp, endfinally, endfilter; null when control continues in the method.</summary>
    public string? ExitKind { get; set; }
    /// <summary>Innermost protected region or handler containing the block.</summary>
    public ExceptionRegionNode? Region { get; set; }
    /// <summary>Stack depth at block entry (-1 when unknown).</summary>
    public int EntryStackDepth { get; set; } = -1;

    public string Name => "B" + Index;
    public ILInstruction Last => Instructions[^1];
}

public sealed class ExceptionRegionNode
{
    public int Index { get; init; }
    /// <summary>try, catch, filter, finally, fault.</summary>
    public required string Kind { get; init; }
    public int Start { get; init; }
    /// <summary>Exclusive end offset.</summary>
    public int End { get; init; }
    public string? CatchType { get; init; }
    /// <summary>For handlers: the protected region they belong to.</summary>
    public ExceptionRegionNode? Try { get; init; }
    public ExceptionRegionNode? Parent { get; set; }
    public List<ExceptionRegionNode> Children { get; } = new();
    public List<BasicBlock> Blocks { get; } = new();

    public bool Contains(int offset) => offset >= Start && offset < End;

    public string Title => Kind switch
    {
        "catch" => "catch " + (CatchType ?? "object"),
        _ => Kind,
    };
}

public sealed class LoopInfo
{
    public required BasicBlock Header { get; init; }
    public required List<BasicBlock> Latches { get; init; }
    public required List<BasicBlock> Body { get; init; }
}

public sealed class ControlFlowGraph
{
    public required List<BasicBlock> Blocks { get; init; }
    public required List<ExceptionRegionNode> Regions { get; init; }
    public List<LoopInfo> Loops { get; } = new();
    public int CyclomaticComplexity { get; set; }
    public List<string> Warnings { get; } = new();

    public IEnumerable<BasicBlock> Unreachable => Blocks.Where(b => !b.Reachable);
    public IEnumerable<BasicBlock> Exits => Blocks.Where(b => b.ExitKind is "return" or "throw" or "rethrow" or "jmp");
    public IEnumerable<CfgEdge> Edges => Blocks.SelectMany(b => b.Successors);

    /// <summary>Region tree flattened in pre-order.</summary>
    public IEnumerable<ExceptionRegionNode> AllRegions()
    {
        var stack = new Stack<ExceptionRegionNode>(Regions.AsEnumerable().Reverse());
        while (stack.Count > 0)
        {
            var r = stack.Pop();
            yield return r;
            for (int i = r.Children.Count - 1; i >= 0; i--) stack.Push(r.Children[i]);
        }
    }
}
