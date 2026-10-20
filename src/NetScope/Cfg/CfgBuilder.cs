using System.Reflection.Metadata;
using NetScope.IL;
using NetScope.Metadata;

namespace NetScope.Cfg;

public interface ICfgBuilder
{
    ControlFlowGraph Build(MethodIL il, MemberResolver resolver);
}

/// <summary>Splits IL into basic blocks and computes edges, exception regions, loops and complexity.</summary>
public sealed class CfgBuilder : ICfgBuilder
{
    public ControlFlowGraph Build(MethodIL il, MemberResolver resolver)
    {
        var ins = il.Instructions;
        var warnings = new List<string>();
        var indexByOffset = new Dictionary<int, int>(ins.Count);
        for (int i = 0; i < ins.Count; i++) indexByOffset[ins[i].Offset] = i;
        int codeSize = ins.Count == 0 ? 0 : ins[^1].NextOffset;

        // 1. Leaders.
        var leader = new bool[ins.Count];
        if (ins.Count > 0) leader[0] = true;
        void Mark(int offset, string what)
        {
            if (indexByOffset.TryGetValue(offset, out var i)) leader[i] = true;
            else if (offset != codeSize) warnings.Add($"IL_{offset:X4}: {what} is not at an instruction boundary");
        }
        for (int i = 0; i < ins.Count; i++)
        {
            var x = ins[i];
            if (x.IsBranch) Mark(x.BranchTarget, "branch target");
            else if (x.IsSwitch) foreach (var t in x.SwitchTargets) Mark(t, "switch target");
            if ((x.EndsBlock) && i + 1 < ins.Count) leader[i + 1] = true;
        }
        var ehRegions = il.Body.ExceptionRegions;
        foreach (var er in ehRegions)
        {
            Mark(er.TryOffset, "try start");
            Mark(er.TryOffset + er.TryLength, "try end");
            Mark(er.HandlerOffset, "handler start");
            Mark(er.HandlerOffset + er.HandlerLength, "handler end");
            if (er.Kind == ExceptionRegionKind.Filter) Mark(er.FilterOffset, "filter start");
        }

        // 2. Blocks.
        var blocks = new List<BasicBlock>();
        var blockByOffset = new Dictionary<int, BasicBlock>();
        for (int i = 0; i < ins.Count;)
        {
            int j = i + 1;
            while (j < ins.Count && !leader[j]) j++;
            var list = ins.GetRange(i, j - i);
            var b = new BasicBlock { Index = blocks.Count, StartOffset = list[0].Offset, EndOffset = list[^1].NextOffset - 1, Instructions = list };
            blocks.Add(b);
            blockByOffset[b.StartOffset] = b;
            i = j;
        }

        // 3. Edges.
        void AddEdge(BasicBlock from, int targetOffset, EdgeKind kind, int caseIndex = 0, string? condition = null)
        {
            if (!blockByOffset.TryGetValue(targetOffset, out var to)) return;
            var e = new CfgEdge { From = from, To = to, Kind = kind, CaseIndex = caseIndex, Condition = condition };
            from.Successors.Add(e);
            to.Predecessors.Add(e);
        }
        for (int bi = 0; bi < blocks.Count; bi++)
        {
            var b = blocks[bi];
            var last = b.Last;
            int next = last.NextOffset;
            bool hasNext = bi + 1 < blocks.Count;
            if (last.IsConditionalBranch)
            {
                AddEdge(b, last.BranchTarget, EdgeKind.True);
                if (hasNext) AddEdge(b, next, EdgeKind.False);
            }
            else if (last.IsSwitch)
            {
                var targets = last.SwitchTargets;
                for (int c = 0; c < targets.Length; c++) AddEdge(b, targets[c], EdgeKind.Case, c);
                if (hasNext) AddEdge(b, next, EdgeKind.Default);
            }
            else if (last.IsLeave) AddEdge(b, last.BranchTarget, EdgeKind.Leave);
            else if (last.IsUnconditionalBranch) AddEdge(b, last.BranchTarget, EdgeKind.Jump);
            else if (last.IsMethodExit || last.IsHandlerExit)
            {
                b.ExitKind = last.Name switch
                {
                    "ret" => "return",
                    "endfinally" => "endfinally",
                    _ => last.Name,
                };
            }
            else if (hasNext) AddEdge(b, next, EdgeKind.Fallthrough);
            else warnings.Add($"IL_{last.Offset:X4}: control falls off the end of the method");
        }

        // 4. Exception regions as a tree, plus edges from protected regions to their handlers.
        var regions = BuildRegions(il, resolver, ehRegions);
        var all = new List<ExceptionRegionNode>();
        foreach (var root in regions) Flatten(root, all);
        foreach (var b in blocks)
        {
            ExceptionRegionNode? inner = null;
            foreach (var r in all)
                if (r.Contains(b.StartOffset) && (inner == null || (r.End - r.Start) <= (inner.End - inner.Start))) inner = r;
            b.Region = inner;
            inner?.Blocks.Add(b);
        }
        foreach (var handler in all.Where(r => r.Try != null))
        {
            if (!blockByOffset.TryGetValue(handler.Try!.Start, out var tryStart)) continue;
            AddEdge(tryStart, handler.Start, EdgeKind.Exception, condition: handler.Title);
        }

        var cfg = new ControlFlowGraph { Blocks = blocks, Regions = regions };
        cfg.Warnings.AddRange(warnings);
        if (blocks.Count == 0)
        {
            cfg.CyclomaticComplexity = 1;
            return cfg;
        }

        FindLoopsAndReachability(cfg);
        cfg.CyclomaticComplexity = Complexity(cfg);
        ComputeStackDepths(cfg, il, resolver);
        AnnotateConditions(cfg, il, resolver);
        return cfg;
    }

    private static void Flatten(ExceptionRegionNode node, List<ExceptionRegionNode> list)
    {
        list.Add(node);
        foreach (var c in node.Children) Flatten(c, list);
    }

    private static List<ExceptionRegionNode> BuildRegions(MethodIL il, MemberResolver resolver, IReadOnlyList<ExceptionRegion> ehRegions)
    {
        var nodes = new List<ExceptionRegionNode>();
        var tries = new Dictionary<(int, int), ExceptionRegionNode>();
        foreach (var er in ehRegions)
        {
            if (!tries.TryGetValue((er.TryOffset, er.TryLength), out var tryNode))
            {
                tryNode = new ExceptionRegionNode { Index = nodes.Count, Kind = "try", Start = er.TryOffset, End = er.TryOffset + er.TryLength };
                tries[(er.TryOffset, er.TryLength)] = tryNode;
                nodes.Add(tryNode);
            }
            switch (er.Kind)
            {
                case ExceptionRegionKind.Catch:
                    string catchType;
                    try { catchType = resolver.ResolveType(er.CatchType, il.Context).Display; }
                    catch (Exception) { catchType = "?"; }
                    nodes.Add(new ExceptionRegionNode { Index = nodes.Count, Kind = "catch", Start = er.HandlerOffset, End = er.HandlerOffset + er.HandlerLength, CatchType = catchType, Try = tryNode });
                    break;
                case ExceptionRegionKind.Filter:
                    nodes.Add(new ExceptionRegionNode { Index = nodes.Count, Kind = "filter", Start = er.FilterOffset, End = er.HandlerOffset, Try = tryNode });
                    nodes.Add(new ExceptionRegionNode { Index = nodes.Count, Kind = "catch", Start = er.HandlerOffset, End = er.HandlerOffset + er.HandlerLength, CatchType = "when filter", Try = tryNode });
                    break;
                case ExceptionRegionKind.Finally:
                    nodes.Add(new ExceptionRegionNode { Index = nodes.Count, Kind = "finally", Start = er.HandlerOffset, End = er.HandlerOffset + er.HandlerLength, Try = tryNode });
                    break;
                case ExceptionRegionKind.Fault:
                    nodes.Add(new ExceptionRegionNode { Index = nodes.Count, Kind = "fault", Start = er.HandlerOffset, End = er.HandlerOffset + er.HandlerLength, Try = tryNode });
                    break;
            }
        }

        // Nest by containment: outer regions first, then by start offset.
        var sorted = nodes.OrderBy(n => n.Start).ThenByDescending(n => n.End - n.Start).ThenBy(n => n.Index).ToList();
        var roots = new List<ExceptionRegionNode>();
        var stack = new Stack<ExceptionRegionNode>();
        foreach (var n in sorted)
        {
            while (stack.Count > 0 && !(n.Start >= stack.Peek().Start && n.End <= stack.Peek().End)) stack.Pop();
            if (stack.Count > 0)
            {
                n.Parent = stack.Peek();
                stack.Peek().Children.Add(n);
            }
            else roots.Add(n);
            stack.Push(n);
        }
        return roots;
    }

    /// <summary>Iterative DFS from the entry block: marks reachable blocks and back edges (loops).</summary>
    private static void FindLoopsAndReachability(ControlFlowGraph cfg)
    {
        var blocks = cfg.Blocks;
        var state = new byte[blocks.Count]; // 0 = new, 1 = on stack, 2 = done
        var stack = new Stack<(BasicBlock Block, int Next)>();
        stack.Push((blocks[0], 0));
        state[0] = 1;
        blocks[0].Reachable = true;
        var latches = new Dictionary<BasicBlock, List<BasicBlock>>();

        while (stack.Count > 0)
        {
            var (b, next) = stack.Pop();
            if (next < b.Successors.Count)
            {
                stack.Push((b, next + 1));
                var e = b.Successors[next];
                var to = e.To;
                if (state[to.Index] == 0)
                {
                    state[to.Index] = 1;
                    to.Reachable = true;
                    stack.Push((to, 0));
                }
                else if (state[to.Index] == 1 && e.Kind != EdgeKind.Exception)
                {
                    e.IsBackEdge = true;
                    to.IsLoopHeader = true;
                    if (!latches.TryGetValue(to, out var l)) latches[to] = l = new List<BasicBlock>();
                    if (!l.Contains(b)) l.Add(b);
                }
            }
            else state[b.Index] = 2;
        }

        foreach (var (header, ls) in latches.OrderBy(kv => kv.Key.Index))
        {
            // Natural loop body: blocks that reach a latch without passing the header.
            var body = new HashSet<BasicBlock> { header };
            var work = new Stack<BasicBlock>(ls.Where(body.Add));
            while (work.Count > 0)
            {
                var b = work.Pop();
                foreach (var p in b.Predecessors)
                    if (p.Kind != EdgeKind.Exception && p.From.Reachable && body.Add(p.From)) work.Push(p.From);
            }
            cfg.Loops.Add(new LoopInfo { Header = header, Latches = ls.OrderBy(b => b.Index).ToList(), Body = body.OrderBy(b => b.Index).ToList() });
        }
    }

    /// <summary>McCabe complexity E - N + 2 over reachable blocks, with a virtual exit node.</summary>
    private static int Complexity(ControlFlowGraph cfg)
    {
        int nodes = 1, edges = 0;
        foreach (var b in cfg.Blocks)
        {
            if (!b.Reachable) continue;
            nodes++;
            int outgoing = b.Successors.Count(e => e.To.Reachable);
            edges += outgoing == 0 ? 1 : outgoing;
        }
        return Math.Max(1, edges - nodes + 2);
    }

    private static void ComputeStackDepths(ControlFlowGraph cfg, MethodIL il, MemberResolver resolver)
    {
        var work = new Queue<BasicBlock>();
        void Seed(BasicBlock b, int depth)
        {
            if (b.EntryStackDepth >= 0) return;
            b.EntryStackDepth = depth;
            work.Enqueue(b);
        }
        Seed(cfg.Blocks[0], 0);
        foreach (var r in cfg.AllRegions())
        {
            var start = cfg.Blocks.FirstOrDefault(b => b.StartOffset == r.Start);
            if (start == null) continue;
            Seed(start, r.Kind is "catch" or "filter" ? 1 : 0);
        }
        while (work.Count > 0)
        {
            var b = work.Dequeue();
            int depth = b.EntryStackDepth;
            foreach (var x in b.Instructions)
            {
                var (pop, push) = StackEffect.Of(x, resolver, il.ReturnType);
                depth = pop < 0 ? 0 : Math.Max(0, depth - pop) + push;
            }
            foreach (var e in b.Successors)
                if (e.Kind != EdgeKind.Exception) Seed(e.To, e.Kind == EdgeKind.Leave ? 0 : depth);
        }
    }

    /// <summary>Rebuilds branch conditions as expressions, e.g. "user != null".</summary>
    private static void AnnotateConditions(ControlFlowGraph cfg, MethodIL il, MemberResolver resolver)
    {
        var evaluator = new SymbolicEvaluator(il, resolver, threeAddress: false);
        foreach (var b in cfg.Blocks)
        {
            if (!b.Last.IsConditionalBranch) continue;
            try
            {
                var entry = EntryStack(b);
                var result = evaluator.Run(b.Instructions, entry);
                if (result.Condition == null) continue;
                var taken = SymbolicEvaluator.Truthy(result.Condition);
                foreach (var e in b.Successors)
                {
                    if (e.Kind == EdgeKind.True) e.Condition = Shorten(taken.Text);
                    else if (e.Kind == EdgeKind.False) e.Condition = Shorten(taken.Negated ?? "!(" + taken.Text + ")");
                }
            }
            catch (Exception)
            {
                // Labels are cosmetic; keep true/false.
            }
        }
    }

    internal static List<Expr> EntryStack(BasicBlock b)
    {
        var entry = new List<Expr>();
        bool exceptionObject = b.Region is { Kind: "catch" or "filter" } r && r.Start == b.StartOffset;
        for (int i = 0; i < Math.Max(0, b.EntryStackDepth); i++)
            entry.Add(exceptionObject && i == 0 ? Expr.Name("ex", Hint.Reference) : Expr.Name("s" + i));
        return entry;
    }

    private static string Shorten(string text) => text.Length <= 80 ? text : text.Substring(0, 77) + "...";
}
