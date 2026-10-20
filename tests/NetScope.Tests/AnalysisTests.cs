using System.Text.RegularExpressions;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.Disassembler;
using NetScope.Asm;
using NetScope.Calls;
using NetScope.Cfg;
using NetScope.IL;
using NetScope.Pseudo;
using Xunit;

namespace NetScope.Tests;

public class AnalysisTests
{
    private static (Loading.LoadedAssembly Asm, Metadata.MemberResolver R, MethodIL IL) Load(string fullName)
    {
        var (asm, resolver, methods) = TestEnv.LoadSamples();
        var m = methods.First(x => x.FullName == fullName);
        var il = MethodIL.Load(m, resolver, out var error, out _) ?? throw new InvalidOperationException(error);
        return (asm, resolver, il);
    }

    /// <summary>Offsets and opcodes must match an independent ildasm-style disassembler (ILSpy) for every method.</summary>
    [Fact]
    public void ILListingMatchesReferenceDisassembler()
    {
        var (asm, resolver, methods) = TestEnv.LoadSamples();
        using (asm)
        {
            var provider = new AsmProvider(resolver, false, Array.Empty<string>(), TimeSpan.FromSeconds(1));
            int compared = 0;
            foreach (var m in methods.Where(m => m.HasBody))
            {
                var il = MethodIL.Load(m, resolver, out _, out _)!;
                var ours = provider.GetIL(il).Lines.Select(l => $"IL_{l.Offset:x4}: {l.OpCode}").ToList();

                var text = new StringWriter();
                var output = new PlainTextOutput(text);
                new ReflectionDisassembler(output, CancellationToken.None).DisassembleMethod(asm.DecompilerFile, m.Handle);
                var reference = Regex.Matches(text.ToString(), @"^\s*IL_([0-9a-f]{4}):\s+([a-z0-9.]+)", RegexOptions.Multiline)
                    .Select(x => $"IL_{x.Groups[1].Value}: {x.Groups[2].Value}").ToList();

                Assert.Equal(reference, ours);
                compared++;
            }
            Assert.True(compared > 40);
        }
    }

    [Fact]
    public void ILOperandsAreResolved()
    {
        var (asm, resolver, il) = Load("MyApp.Auth.LoginService::Login");
        using (asm)
        {
            var lines = new AsmProvider(resolver, false, Array.Empty<string>(), TimeSpan.FromSeconds(1)).GetIL(il).Lines;
            Assert.Contains(lines, l => l.OpCode == "callvirt" && l.Operand == "instance class MyApp.Db.User MyApp.Db.Users::Find(string)");
            Assert.Contains(lines, l => l.OpCode == "call" && l.Operand == "bool [System.Runtime]System.String::op_Equality(string, string)");
            Assert.Contains(lines, l => l.OpCode == "brtrue.s" && l.Operand == "IL_0005");
        }
    }

    [Fact]
    public void CallsAreClassifiedAndReverseIndexed()
    {
        var (asm, resolver, methods) = TestEnv.LoadSamples();
        using (asm)
        {
            var analyzer = new CallAnalyzer();
            var index = new CallIndex();
            index.AddDefinitions(methods);
            foreach (var m in methods.Where(m => m.HasBody))
                index.AddCalls(m, analyzer.Analyze(MethodIL.Load(m, resolver, out _, out _)!, resolver));

            var login = methods.First(m => m.FullName == "MyApp.Auth.LoginService::Login");
            var calls = analyzer.Analyze(MethodIL.Load(login, resolver, out _, out _)!, resolver);
            var find = calls.Single(c => c.Display == "MyApp.Db.Users::Find(string)");
            Assert.Equal("callvirt", find.Kind);
            Assert.True(find.IsInternal);
            var eq = calls.Single(c => c.Target!.Name == "op_Equality");
            Assert.Equal("external", eq.Scope);
            Assert.Equal("System.Runtime", eq.Target!.Assembly);

            var callers = index.CallersOf(login);
            Assert.Contains(callers, c => c.Caller.FullName == "MyApp.Auth.AdminLoginService::Login" && c.Kind == "call");

            // ldftn of lambdas is reported as a call site.
            var lambdas = methods.First(m => m.FullName == "MyApp.Flow.Features::Lambdas");
            var lambdaCalls = analyzer.Analyze(MethodIL.Load(lambdas, resolver, out _, out _)!, resolver);
            Assert.Contains(lambdaCalls, c => c.Kind == "ldftn");
            Assert.Contains(lambdaCalls, c => c.Kind == "newobj");
        }
    }

    [Fact]
    public void CfgOfLoginMatchesSpecExample()
    {
        var (asm, resolver, il) = Load("MyApp.Auth.LoginService::Login");
        using (asm)
        {
            var cfg = new CfgBuilder().Build(il, resolver);
            Assert.Equal(3, cfg.Blocks.Count);
            Assert.Equal(2, cfg.CyclomaticComplexity);
            var b0 = cfg.Blocks[0];
            Assert.Contains(b0.Successors, e => e.Kind == EdgeKind.True && e.Condition == "user != null");
            Assert.Contains(b0.Successors, e => e.Kind == EdgeKind.False && e.Condition == "user == null");
            Assert.Equal(2, cfg.Exits.Count());
        }
    }

    [Fact]
    public void CfgFindsLoop()
    {
        var (asm, resolver, il) = Load("MyApp.Flow.Branches::Loop");
        using (asm)
        {
            var cfg = new CfgBuilder().Build(il, resolver);
            var loop = Assert.Single(cfg.Loops);
            Assert.True(loop.Header.IsLoopHeader);
            Assert.Contains(cfg.Edges, e => e.IsBackEdge);
            Assert.Equal(3, cfg.CyclomaticComplexity);
            Assert.Empty(cfg.Unreachable);
        }
    }

    [Fact]
    public void CfgSwitchHasCasesAndDefault()
    {
        var (asm, resolver, il) = Load("MyApp.Flow.Branches::Switch");
        using (asm)
        {
            var cfg = new CfgBuilder().Build(il, resolver);
            var edges = cfg.Blocks[0].Successors;
            Assert.Equal(new[] { 0, 1, 2, 3 }, edges.Where(e => e.Kind == EdgeKind.Case).Select(e => e.CaseIndex));
            Assert.Single(edges, e => e.Kind == EdgeKind.Default);
            Assert.Equal(5, cfg.CyclomaticComplexity);
        }
    }

    [Fact]
    public void CfgNestsExceptionRegions()
    {
        var (asm, resolver, il) = Load("MyApp.Flow.Branches::NestedTry");
        using (asm)
        {
            var cfg = new CfgBuilder().Build(il, resolver);
            var all = cfg.AllRegions().ToList();
            Assert.Equal(new[] { "try", "try", "try", "catch", "finally", "filter", "catch" }, all.Select(r => r.Kind));
            var outerTry = cfg.Regions[0];
            Assert.Equal("try", outerTry.Kind);
            var middle = outerTry.Children[0];
            Assert.Equal(new[] { "try", "catch" }, middle.Children.Select(r => r.Kind));
            Assert.Equal("FormatException", middle.Children[1].CatchType);
            Assert.Contains(cfg.Regions, r => r.Kind == "filter" && r.Try == outerTry);
            Assert.Contains(cfg.Blocks, b => b.ExitKind == "endfinally");
            Assert.Contains(cfg.Blocks, b => b.ExitKind == "endfilter");
            Assert.All(cfg.Blocks, b => Assert.True(b.Reachable));
        }
    }

    [Fact]
    public void ThrowIsAnExit()
    {
        var (asm, resolver, il) = Load("MyApp.Flow.Branches::Throws");
        using (asm)
        {
            var cfg = new CfgBuilder().Build(il, resolver);
            Assert.Contains(cfg.Exits, b => b.ExitKind == "throw");
            Assert.Single(cfg.Loops);
        }
    }

    [Fact]
    public void FallbackPseudocodeIsThreeAddress()
    {
        var (asm, resolver, il) = Load("MyApp.Auth.LoginService::Login");
        using (asm)
        {
            var cfg = new CfgBuilder().Build(il, resolver);
            var text = FallbackPseudo.Render(il, cfg, resolver);
            Assert.Contains("if (user != null) goto B2;", text);
            Assert.Contains("t0 = this._users;", text);
            Assert.Contains("t1 = t0.Find(user);", text);
            Assert.Contains("return", text);
        }
    }

    [Fact]
    public void DecompilerCoversAtLeast95PercentOfMethods()
    {
        var (asm, resolver, methods) = TestEnv.LoadSamples();
        using (asm)
        using (var refs = new Loading.ReferenceResolver(new[] { asm }, Array.Empty<string>()))
        {
            var gen = new PseudoGenerator(asm, refs.For(asm), resolver, raw: false, TimeSpan.FromSeconds(20));
            int ok = 0;
            foreach (var m in methods)
            {
                var r = gen.Generate(m, MethodIL.Load(m, resolver, out _, out _), null);
                if (r.Source == "decompiler") ok++;
            }
            Assert.True(ok >= methods.Count * 0.95, $"decompiled {ok} of {methods.Count}");
        }
    }

    [Fact]
    public void RawModeShowsStateMachine()
    {
        var (asm, resolver, methods) = TestEnv.LoadSamples();
        using (asm)
        using (var refs = new Loading.ReferenceResolver(new[] { asm }, Array.Empty<string>()))
        {
            var m = methods.First(x => x.FullName == "MyApp.Flow.Features::ComputeAsync");
            var restored = new PseudoGenerator(asm, refs.For(asm), resolver, false, TimeSpan.FromSeconds(20)).Generate(m, null, null).Text;
            var raw = new PseudoGenerator(asm, refs.For(asm), resolver, true, TimeSpan.FromSeconds(20)).Generate(m, null, null).Text;
            Assert.Contains("await", restored);
            Assert.DoesNotContain("await", raw);
            Assert.Contains("Start", raw);
        }
    }
}
