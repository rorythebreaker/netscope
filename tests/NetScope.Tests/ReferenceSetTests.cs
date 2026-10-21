using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Text.RegularExpressions;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;
using NetScope.Asm;
using NetScope.Catalog;
using NetScope.Cfg;
using NetScope.IL;
using NetScope.Loading;
using NetScope.Metadata;
using NetScope.Pseudo;
using Xunit;

namespace NetScope.Tests;

/// <summary>
/// Acceptance on the reference set (TZ-NetScope-3 §3.4): reference output for every assembly, the method list and
/// IL against ildasm, method kinds and decompilation against ILSpy, pseudocode coverage, and the CFG of the
/// hand-written IL and switch assemblies. Known differences are listed in docs/reference-set.md.
/// </summary>
public class ReferenceSetTests
{
    public static TheoryData<string> Ids => ReferenceSet.Ids;

    // ---- reference output ---------------------------------------------------------------------------------

    private static readonly (string Kind, string Extension, string[] Options)[] OutputKinds =
    {
        ("methods", "txt", new[] { "--methods" }),
        ("calls", "txt", new[] { "--calls" }),
        ("cfg", "txt", new[] { "--cfg" }),
        ("il", "txt", new[] { "--il" }),
        ("pseudo", "txt", new[] { "--pseudo" }),
        ("json", "json", new[] { "--methods", "--calls", "--format", "json" }), // CFG and IL are covered by the text files
        ("native", "txt", new[] { "--native" }), // ReadyToRun copies only
    };

    public static TheoryData<string, string> GoldenCases
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var id in ReferenceSet.IdList)
                foreach (var (kind, _, _) in OutputKinds)
                    if (kind != "native" || id.Contains("-R2R-", StringComparison.Ordinal)) data.Add(id, kind);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(GoldenCases))]
    public void OutputMatchesReference(string id, string kind)
    {
        var asm = ReferenceSet.Get(id);
        var (_, extension, options) = OutputKinds.Single(k => k.Kind == kind);
        var r = TestEnv.Run(asm.Args(options));
        Assert.True(r.ExitCode == 0, $"exit {r.ExitCode}: {r.Stderr}");

        var actual = Normalize(r.Stdout);
        var dir = Path.Combine(TestEnv.GoldenDir, "Reference");
        var path = Path.Combine(dir, kind == extension ? $"{id}.{extension}" : $"{id}.{kind}.{extension}");
        if (Environment.GetEnvironmentVariable("NETSCOPE_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, actual);
            return;
        }
        Assert.True(File.Exists(path), $"golden file {Path.GetFileName(path)} is missing; run with NETSCOPE_UPDATE_GOLDEN=1");
        Assert.Equal(Normalize(File.ReadAllText(path)), actual);
    }

    /// <summary>Drops machine-specific lines (absolute paths, tool version) and normalizes line endings.</summary>
    private static string Normalize(string text) =>
        string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Where(l =>
            !l.StartsWith("Path     :", StringComparison.Ordinal) &&
            !Regex.IsMatch(l, "^\\s*\"path\": ") &&
            !l.StartsWith("  \"version\": ", StringComparison.Ordinal)));

    // ---- ildasm -------------------------------------------------------------------------------------------

    /// <summary>§3.4: the method list, and every instruction with its offset, match ildasm for every method.</summary>
    [Theory]
    [MemberData(nameof(Ids))]
    public void MethodsAndILMatchIldasm(string id)
    {
        var asm = ReferenceSet.Get(id);
        using var loaded = Open(asm, out var resolver, out var methods);
        var provider = new AsmProvider(resolver, false, Array.Empty<string>(), TimeSpan.FromSeconds(1));

        // Documented difference: ConfuserEx nests types in a type that does not exist. The Windows build of ildasm
        // aborts on that (its protection against ildasm); the Unix build reads the file. Where ildasm cannot, the
        // independent reference for the obfuscated file is ILSpy's disassembler.
        Dictionary<int, List<string>> reference;
        if (Ildasm.TryDisassemble(asm.Path, out var parsed, out var error))
            reference = parsed;
        else if (asm.Obfuscated)
            reference = methods.ToDictionary(m => m.Token, m => IlspyListing(loaded, m.Handle));
        else
            throw new Xunit.Sdk.XunitException(error);

        Assert.Equal(reference.Keys.OrderBy(t => t), methods.Select(m => m.Token).OrderBy(t => t));

        var differences = new List<string>();
        foreach (var m in methods)
        {
            var ours = new List<string>();
            if (m.HasBody && MethodIL.Load(m, resolver, out _, out _) is { } il)
                ours.AddRange(provider.GetIL(il).Lines.Select(l => $"IL_{l.Offset:x4}: {l.OpCode}"));
            var theirs = reference[m.Token];
            if (!ours.SequenceEqual(theirs))
                differences.Add($"{m.FullName} (0x{m.Token:X8}): first difference at {FirstDifference(ours, theirs)}");
        }
        Assert.True(differences.Count == 0, string.Join("\n", differences));
    }

    private static List<string> IlspyListing(LoadedAssembly asm, MethodDefinitionHandle handle)
    {
        var text = new StringWriter();
        try { new ICSharpCode.Decompiler.Disassembler.ReflectionDisassembler(new PlainTextOutput(text), CancellationToken.None).DisassembleMethod(asm.DecompilerFile, handle); }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException) { return new List<string>(); }
        return Regex.Matches(text.ToString(), @"^\s*IL_([0-9a-f]{4}):\s+([a-z0-9.]+)", RegexOptions.Multiline)
            .Select(x => $"IL_{x.Groups[1].Value}: {x.Groups[2].Value}").ToList();
    }

    private static string FirstDifference(List<string> ours, List<string> theirs)
    {
        for (int i = 0; ; i++)
        {
            var a = i < ours.Count ? ours[i] : "<end>";
            var b = i < theirs.Count ? theirs[i] : "<end>";
            if (a != b) return $"#{i}: netscope '{a}', ildasm '{b}'";
        }
    }

    /// <summary>Runs ildasm from the Microsoft.NETCore.ILDAsm package and collects the IL of each method by token.</summary>
    private static class Ildasm
    {
        private static readonly Regex MethodLine = new(@"^\s*\.method\s+/\*([0-9A-F]{8})\*/", RegexOptions.Compiled);
        private static readonly Regex InstructionLine = new(@"^\s*IL_([0-9a-f]{4}):\s+([a-z0-9.]+)", RegexOptions.Compiled);

        public static bool TryDisassemble(string assembly, out Dictionary<int, List<string>> methods, out string error)
        {
            var exe = Path.Combine(Directory(), OperatingSystem.IsWindows() ? "ildasm.exe" : "ildasm");
            Assert.True(File.Exists(exe), "ildasm not found at " + exe + " (restored by NetScope.Tests.csproj)");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(exe, File.GetUnixFileMode(exe) | UnixFileMode.UserExecute); // NuGet does not keep the mode

            methods = new Dictionary<int, List<string>>();
            var output = Path.Combine(TestEnv.TempDir(), "out.il");
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            // '-' rather than '/': on Unix an argument starting with '/' is a path.
            foreach (var a in new[] { "-utf8", "-tokens", "-out=" + output, assembly }) psi.ArgumentList.Add(a);
            using (var p = Process.Start(psi)!)
            {
                var stderr = p.StandardError.ReadToEndAsync();
                var stdout = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                error = $"ildasm exited with {p.ExitCode}: {stderr.Result} {stdout}".Trim();
                if (p.ExitCode != 0 || !File.Exists(output)) return false;
            }

            List<string>? current = null;
            foreach (var line in File.ReadLines(output, Encoding.UTF8))
            {
                var m = MethodLine.Match(line);
                if (m.Success)
                {
                    current = new List<string>();
                    methods.Add(Convert.ToInt32(m.Groups[1].Value, 16), current);
                    continue;
                }
                var i = InstructionLine.Match(line);
                if (i.Success && current != null) current.Add($"IL_{i.Groups[1].Value}: {i.Groups[2].Value}");
            }
            return true;
        }

        private static string Directory() =>
            typeof(ReferenceSetTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "IldasmDir").Value!;
    }

    // ---- ILSpy --------------------------------------------------------------------------------------------

    /// <summary>The kinds both tools define: NetScope's classification must agree with ILSpy's type system.</summary>
    private static readonly HashSet<string> ComparableKinds = new(StringComparer.Ordinal)
    {
        "ctor", "cctor", "finalizer", "property-get", "property-set", "event-add", "event-remove", "operator", "conversion",
        "static", "instance", "abstract", "virtual", "override", "sealed", "generic", "extension", "async", "iterator",
    };

    /// <summary>§3.4: the method list and the method kinds agree with ILSpy; lambdas and local functions are the
    /// methods ILSpy folds into their callers.</summary>
    [Theory]
    [MemberData(nameof(Ids))]
    public void MethodKindsMatchILSpy(string id)
    {
        var asm = ReferenceSet.Get(id);
        using var loaded = Open(asm, out _, out var methods);
        using var refs = new ReferenceResolver(new[] { loaded }, asm.RefPaths);
        var ts = new DecompilerTypeSystem(loaded.DecompilerFile, refs.For(loaded), TypeSystemOptions.Default);

        // ILSpy's own enumeration: the methods and accessors of every type definition. Members it synthesizes
        // (no metadata row, nil token) are not methods of the file.
        var ilspyTokens = new HashSet<int>();
        void AddToken(IMethod? m) { if (m != null && !m.MetadataToken.IsNil) ilspyTokens.Add(MetadataTokens.GetToken(m.MetadataToken)); }
        foreach (var type in ts.MainModule.TypeDefinitions)
        {
            foreach (var m in type.Methods) AddToken(m);
            foreach (var p in type.Properties) { AddToken(p.Getter); AddToken(p.Setter); }
            foreach (var e in type.Events) { AddToken(e.AddAccessor); AddToken(e.RemoveAccessor); AddToken(e.InvokeAccessor); }
        }
        Assert.Equal(ilspyTokens.OrderBy(t => t), methods.Select(m => m.Token).OrderBy(t => t));

        var settings = new DecompilerSettings(LanguageVersion.Latest);
        var module = (MetadataModule)ts.MainModule;
        var differences = new List<string>();
        foreach (var m in methods)
        {
            var method = module.GetDefinition(m.Handle);
            var ours = m.Kinds.Where(ComparableKinds.Contains).ToList();
            // Documented difference: an implicit interface implementation is `virtual final newslot` in metadata.
            // NetScope reports the metadata (virtual, sealed); ILSpy reports the C# view (not virtual).
            if (IsInterfaceImplementationShape(m.Attributes) && !method.IsVirtual && !method.IsOverride)
                ours.RemoveAll(k => k is "virtual" or "sealed");
            var oursSorted = ours.OrderBy(k => k, StringComparer.Ordinal);
            var theirs = IlspyKinds(method).OrderBy(k => k, StringComparer.Ordinal);
            if (!oursSorted.SequenceEqual(theirs))
                differences.Add($"{m.FullName} (0x{m.Token:X8}): netscope [{string.Join(", ", oursSorted)}], ilspy [{string.Join(", ", theirs)}]");
            if ((m.Kinds.Contains("lambda") || m.Kinds.Contains("local-function")) && !IsHiddenByILSpy(loaded, m, settings))
                differences.Add($"{m.FullName} (0x{m.Token:X8}): a {(m.Kinds.Contains("lambda") ? "lambda" : "local function")} for NetScope, a visible method for ILSpy");
        }
        Assert.True(differences.Count == 0, string.Join("\n", differences));
    }

    private static bool IsInterfaceImplementationShape(MethodAttributes a) =>
        (a & (MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot | MethodAttributes.Abstract)) ==
        (MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot);

    /// <summary>ILSpy hides a lambda either itself or, for those in a closure class (&lt;&gt;c, DisplayClass),
    /// by hiding the class.</summary>
    private static bool IsHiddenByILSpy(LoadedAssembly asm, MethodModel m, DecompilerSettings settings)
    {
        if (CSharpDecompiler.MemberIsHidden(asm.DecompilerFile, m.Handle, settings)) return true;
        for (var type = m.DeclaringTypeHandle; !type.IsNil; type = asm.Metadata.GetTypeDefinition(type).GetDeclaringType())
            if (CSharpDecompiler.MemberIsHidden(asm.DecompilerFile, type, settings)) return true;
        return false;
    }

    private static IEnumerable<string> IlspyKinds(IMethod m)
    {
        if (m.IsConstructor) yield return m.IsStatic ? "cctor" : "ctor";
        if (m.IsDestructor) yield return "finalizer";
        switch (m.AccessorKind)
        {
            case MethodSemanticsAttributes.Getter: yield return "property-get"; break;
            case MethodSemanticsAttributes.Setter: yield return "property-set"; break;
            case MethodSemanticsAttributes.Adder: yield return "event-add"; break;
            case MethodSemanticsAttributes.Remover: yield return "event-remove"; break;
        }
        if (m.IsOperator) yield return m.Name is "op_Implicit" or "op_Explicit" or "op_CheckedExplicit" ? "conversion" : "operator";
        yield return m.IsStatic ? "static" : "instance";
        if (m.IsAbstract) yield return "abstract";
        if (m.IsVirtual) yield return "virtual";
        if (m.IsOverride) yield return "override";
        if (m.IsSealed) yield return "sealed";
        if (m.TypeParameters.Count > 0) yield return "generic";
        if (m.IsExtensionMethod) yield return "extension";
        var attributes = m.GetAttributes().Select(a => a.AttributeType.FullName).ToHashSet(StringComparer.Ordinal);
        bool asyncIterator = attributes.Contains("System.Runtime.CompilerServices.AsyncIteratorStateMachineAttribute");
        if (asyncIterator || attributes.Contains("System.Runtime.CompilerServices.AsyncStateMachineAttribute")) yield return "async";
        if (asyncIterator || attributes.Contains("System.Runtime.CompilerServices.IteratorStateMachineAttribute")) yield return "iterator";
    }

    /// <summary>§3.4: pseudocode for at least 95% of the methods of every non-obfuscated assembly, never fewer than
    /// ILSpy decompiles on its own; every other method gets the fallback.</summary>
    [Theory]
    [MemberData(nameof(Ids))]
    public void PseudocodeCoversAtLeast95Percent(string id)
    {
        var asm = ReferenceSet.Get(id);
        using var loaded = Open(asm, out var resolver, out var methods);
        using var refs = new ReferenceResolver(new[] { loaded }, asm.RefPaths);
        var generator = new PseudoGenerator(loaded, refs.For(loaded), resolver, raw: false, TimeSpan.FromSeconds(20), new AbandonedDecompilations(4));
        var ilspy = new CSharpDecompiler(loaded.DecompilerFile, refs.For(loaded), new DecompilerSettings(LanguageVersion.Latest) { ThrowOnAssemblyResolveErrors = false });

        int ours = 0, theirs = 0;
        var fallbackWithoutText = new List<string>();
        foreach (var m in methods)
        {
            var il = MethodIL.Load(m, resolver, out _, out _);
            var result = generator.Generate(m, il, il != null ? new CfgBuilder().Build(il, resolver) : null);
            if (result.Source == "decompiler") ours++;
            else if (string.IsNullOrWhiteSpace(result.Text)) fallbackWithoutText.Add(m.FullName);
            try { ilspy.DecompileAsString(m.Handle); theirs++; } catch (Exception) { }
        }

        Assert.True(fallbackWithoutText.Count == 0, "no pseudocode at all for: " + string.Join(", ", fallbackWithoutText));
        Assert.True(ours >= theirs, $"NetScope decompiled {ours} of {methods.Count}, ILSpy {theirs}");
        if (!asm.Obfuscated)
            Assert.True(ours >= methods.Count * 0.95, $"decompiled {ours} of {methods.Count} ({100.0 * ours / methods.Count:0.#}%)");
    }

    // ---- CFG of assemblies 12 and 13 --------------------------------------------------------------------

    private static ControlFlowGraph Cfg(string id, string fullName)
    {
        var asm = ReferenceSet.Get(id);
        var loaded = new AssemblyLoader().TryLoad(0, asm.Path, out var reason) ?? throw new InvalidOperationException(reason);
        using (loaded)
        {
            var resolver = new MemberResolver(loaded.Metadata, loaded.Name);
            var m = new MethodCatalog().Build(loaded, resolver, MethodFilter.All).Single(x => x.FullName == fullName);
            var il = MethodIL.Load(m, resolver, out var error, out _) ?? throw new InvalidOperationException(error);
            return new CfgBuilder().Build(il, resolver);
        }
    }

    [Fact]
    public void CfgOfFaultHandler()
    {
        var cfg = Cfg("12-HandIL", "Ref.HandIL.Constructs::WithFault");
        Assert.Equal(new[] { "try", "fault" }, cfg.AllRegions().Select(r => r.Kind));
        Assert.Contains(cfg.Blocks[0].Successors, e => e.Kind == EdgeKind.Exception);
        Assert.Contains(cfg.Exits, b => b.ExitKind == "throw");
        Assert.Contains(cfg.Blocks, b => b.ExitKind == "endfinally"); // endfault is the same opcode
        Assert.All(cfg.Blocks, b => Assert.True(b.Reachable));
    }

    [Fact]
    public void CfgOfFilterWithBranches()
    {
        var cfg = Cfg("12-HandIL", "Ref.HandIL.Constructs::WithFilter");
        Assert.Equal(new[] { "try", "filter", "catch" }, cfg.AllRegions().Select(r => r.Kind));
        var filter = cfg.AllRegions().Single(r => r.Kind == "filter");
        Assert.Equal(4, cfg.Blocks.Count(b => b.Region == filter));
        Assert.Single(cfg.Blocks, b => b.ExitKind == "endfilter");
        Assert.Equal(4, cfg.CyclomaticComplexity);
    }

    [Fact]
    public void CfgOfRegionsNestedInHandlers()
    {
        var cfg = Cfg("12-HandIL", "Ref.HandIL.Constructs::NestedInHandlers");
        var outer = Assert.Single(cfg.Regions, r => r.Kind == "try");
        Assert.Equal(new[] { "try", "catch" }, outer.Children.Select(r => r.Kind));
        Assert.Equal(new[] { "try", "fault" }, outer.Children[1].Children.Select(r => r.Kind));      // try/fault inside the catch
        var fin = Assert.Single(cfg.Regions, r => r.Kind == "finally");
        Assert.Equal(new[] { "try", "catch" }, fin.Children.Select(r => r.Kind));                   // try/catch inside the finally
        Assert.All(cfg.Blocks, b => Assert.True(b.Reachable));
    }

    [Fact]
    public void CfgOfSwitchWithoutDefault()
    {
        var cfg = Cfg("12-HandIL", "Ref.HandIL.Constructs::SwitchFallThrough");
        var edges = cfg.Blocks[0].Successors;
        Assert.Equal(new[] { 0, 1, 2, 3 }, edges.Where(e => e.Kind == EdgeKind.Case).Select(e => e.CaseIndex));
        var fallThrough = Assert.Single(edges, e => e.Kind == EdgeKind.Default);
        CfgEdge Case(int n) => edges.Single(e => e.Kind == EdgeKind.Case && e.CaseIndex == n);
        Assert.Same(Case(0).To, fallThrough.To);                                                     // out of range lands on case 0
        Assert.Same(Case(1).To, Case(3).To);
        Assert.Equal(3, cfg.Exits.Count());
    }

    [Fact]
    public void CfgOfLoopAroundSwitch()
    {
        var cfg = Cfg("12-HandIL", "Ref.HandIL.Constructs::SwitchLoop");
        var loop = Assert.Single(cfg.Loops);
        Assert.True(loop.Header.IsLoopHeader);
        Assert.Contains(cfg.Blocks, b => b.Successors.Count(e => e.Kind == EdgeKind.Case) == 3);
        Assert.Empty(cfg.Unreachable);
    }

    [Fact]
    public void CfgOfJmpTailAndUnreachableCode()
    {
        Assert.Contains(Cfg("12-HandIL", "Ref.HandIL.Constructs::Jumper").Exits, b => b.ExitKind == "jmp");
        Assert.Equal(2, Cfg("12-HandIL", "Ref.HandIL.Constructs::Factorial").Exits.Count());
        var rethrows = Cfg("12-HandIL", "Ref.HandIL.Constructs::Rethrows");
        Assert.Contains(rethrows.Exits, b => b.ExitKind == "rethrow");
        Assert.Equal(3, rethrows.Unreachable.Count());
    }

    [Fact]
    public void CfgOfJumpTable()
    {
        var cfg = Cfg("13-Switches", "Ref.Switches.Dispatch::HttpStatus");
        Assert.True(cfg.Blocks.Sum(b => b.Successors.Count(e => e.Kind == EdgeKind.Case)) >= 20, "jump table cases");
        Assert.Empty(cfg.Loops);
        Assert.Empty(cfg.Unreachable);
    }

    [Fact]
    public void CfgOfHashedStringSwitch()
    {
        var cfg = Cfg("13-Switches", "Ref.Switches.Dispatch::Keyword");
        Assert.True(cfg.Blocks.Count >= 21, $"{cfg.Blocks.Count} blocks for 20 string cases, null and default");
        Assert.True(cfg.CyclomaticComplexity >= 21, $"complexity {cfg.CyclomaticComplexity}");
        Assert.Empty(cfg.Loops);
    }

    [Fact]
    public void CfgOfSwitchInsideLoop()
    {
        // Sparse char cases (' ', '\t', '\n', '(', ')', ';'): the compiler emits comparisons, not a jump table.
        var cfg = Cfg("13-Switches", "Ref.Switches.Dispatch::CountTokens");
        Assert.Single(cfg.Loops);
        Assert.True(cfg.CyclomaticComplexity >= 8, $"complexity {cfg.CyclomaticComplexity}");
        Assert.Empty(cfg.Unreachable);
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    private static LoadedAssembly Open(ReferenceAssembly asm, out MemberResolver resolver, out IReadOnlyList<MethodModel> methods)
    {
        var loaded = new AssemblyLoader().TryLoad(0, asm.Path, out var reason) ?? throw new InvalidOperationException(reason);
        resolver = new MemberResolver(loaded.Metadata, loaded.Name);
        methods = new MethodCatalog().Build(loaded, resolver, MethodFilter.All);
        return loaded;
    }
}
