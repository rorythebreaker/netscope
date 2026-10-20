using System.Text.Json;
using Xunit;

namespace NetScope.Tests;

public class SymbolTests
{
    private static string SourceFile => Path.Combine(TestEnv.RepoRoot, "tests", "NetScope.Samples.Pdb", "Calculator.cs");

    /// <summary>1-based line of the first source line containing the text.</summary>
    private static int LineOf(string text) => Array.FindIndex(File.ReadAllLines(SourceFile), l => l.Contains(text, StringComparison.Ordinal)) + 1;

    private static JsonElement Method(JsonDocument doc) => doc.RootElement.GetProperty("assemblies")[0].GetProperty("methods")[0];

    [Fact]
    public void IlListingShowsSourceLinesAndLocalNames()
    {
        var r = TestEnv.Run(TestEnv.PdbSamplePath, "--il", "--filter", "*::SumOfSquares", "--no-color");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("Note     : symbols: NetScope.Samples.Pdb.pdb", r.Stdout);
        Assert.Contains($"// Calculator.cs:{LineOf("int square = value * value;")}", r.Stdout);
        Assert.Contains("[4] int32 square", r.Stdout);
        Assert.Contains("stloc.s    square", r.Stdout);
        // The comment comes right before the first instruction of the sequence point.
        var lines = r.Stdout.Replace("\r\n", "\n").Split('\n');
        int at = Array.FindIndex(lines, l => l.Trim() == $"// Calculator.cs:{LineOf("int total = 0;")}");
        Assert.StartsWith("IL_0001", lines[at + 1].Trim());
    }

    [Fact]
    public void JsonHasSourceOfInstructionsAndBlocks()
    {
        var r = TestEnv.Run(TestEnv.PdbSamplePath, "--il", "--cfg", "--format", "json", "--filter", "*::SumOfSquares");
        using var doc = JsonDocument.Parse(r.Stdout);
        Assert.EndsWith("NetScope.Samples.Pdb.pdb", doc.RootElement.GetProperty("assemblies")[0].GetProperty("symbols").GetString());
        var m = Method(doc);
        var mul = m.GetProperty("il").EnumerateArray().First(i => i.GetProperty("opcode").GetString() == "mul");
        Assert.Equal("Calculator.cs", mul.GetProperty("source").GetProperty("file").GetString());
        Assert.Equal(LineOf("int square = value * value;"), mul.GetProperty("source").GetProperty("line").GetInt32());
        var blocks = m.GetProperty("blocks").EnumerateArray().ToList();
        Assert.All(blocks, b => Assert.Equal("Calculator.cs", b.GetProperty("source").GetProperty("file").GetString()));
        var loopBody = blocks.Single(b => b.GetProperty("instructions").EnumerateArray().Any(i => i.GetInt32() == mul.GetProperty("offset").GetInt32()));
        Assert.True(loopBody.GetProperty("source").GetProperty("line").GetInt32() <= LineOf("int square"));
        Assert.True(loopBody.GetProperty("source").GetProperty("endLine").GetInt32() >= LineOf("total += square;"));
    }

    [Fact]
    public void PseudocodeUsesNamesFromThePdb()
    {
        var with = TestEnv.Run(TestEnv.PdbSamplePath, "--pseudo", "--filter", "*::SumOfSquares", "--no-color").Stdout;
        var without = TestEnv.Run(TestEnv.PdbSamplePath, "--pseudo", "--no-pdb", "--filter", "*::SumOfSquares", "--no-color").Stdout;
        Assert.Contains("int square = value * value;", with);
        Assert.Contains("total += square;", with);
        Assert.DoesNotContain("square", without);
        Assert.DoesNotContain("symbols:", without);
    }

    [Fact]
    public void SourceOptionPrintsTheSourceText()
    {
        var r = TestEnv.Run(TestEnv.PdbSamplePath, "--il", "--source", "--filter", "*::SumOfSquares", "--no-color");
        Assert.Contains($"{LineOf("int square"),5} |             int square = value * value;", r.Stdout);

        // A document path from another machine is found by name under --source-path.
        var sources = TestEnv.TempDir();
        File.Copy(SourceFile, Path.Combine(sources, "Calculator.cs"));
        var lines = new Symbols.SourceFiles(new[] { sources }).Lines("/elsewhere/build/Calculator.cs", 1, 1);
        Assert.Equal(new[] { "namespace PdbSample;" }, lines);
        Assert.Null(new Symbols.SourceFiles(Array.Empty<string>()).Lines("/elsewhere/build/Calculator.cs", 1, 1));
    }

    [Fact]
    public void PdbIsFoundInPdbPath()
    {
        var dir = TestEnv.TempDir();
        var dll = Path.Combine(dir, "NetScope.Samples.Pdb.dll");
        File.Copy(TestEnv.PdbSamplePath, dll);
        var withoutPath = TestEnv.Run(dll, "--il", "--filter", "*::SumOfSquares", "--no-color");
        Assert.DoesNotContain("// Calculator.cs", withoutPath.Stdout);

        var symbols = Directory.CreateDirectory(Path.Combine(dir, "symbols")).FullName;
        File.Copy(Path.ChangeExtension(TestEnv.PdbSamplePath, ".pdb"), Path.Combine(symbols, "NetScope.Samples.Pdb.pdb"));
        var withPath = TestEnv.Run(dll, "--il", "--pdb-path", symbols, "--filter", "*::SumOfSquares", "--no-color");
        Assert.Contains("// Calculator.cs", withPath.Stdout);
    }

    [Fact]
    public void PdbWithAnotherIdIsRejected()
    {
        var dir = TestEnv.TempDir();
        var dll = Path.Combine(dir, "NetScope.Samples.Pdb.dll");
        File.Copy(TestEnv.PdbSamplePath, dll);
        // A valid Portable PDB, but of another assembly.
        File.Copy(Path.ChangeExtension(TestEnv.HostilePath, ".pdb"), Path.Combine(dir, "NetScope.Samples.Pdb.pdb"));
        var r = TestEnv.Run(dll, "--il", "--filter", "*::SumOfSquares", "--no-color");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("does not belong to this assembly", r.Stderr);
        Assert.DoesNotContain("// Calculator.cs", r.Stdout);
        Assert.DoesNotContain("square", r.Stdout);
    }

    [Fact]
    public void WindowsPdbIsReportedAndIgnored()
    {
        var dir = TestEnv.TempDir();
        var dll = Path.Combine(dir, "NetScope.Samples.Pdb.dll");
        File.Copy(TestEnv.PdbSamplePath, dll);
        File.WriteAllBytes(Path.Combine(dir, "NetScope.Samples.Pdb.pdb"), "Microsoft C/C++ MSF 7.00\r\n\u001aDS\0\0\0"u8.ToArray().Concat(new byte[64]).ToArray());
        var r = TestEnv.Run(dll, "--il", "--filter", "*::SumOfSquares", "--no-color");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("Windows PDB", r.Stderr);
        Assert.DoesNotContain("// Calculator.cs", r.Stdout);
    }

    /// <summary>NetScope itself is built with an embedded PDB.</summary>
    [Fact]
    public void EmbeddedPdbIsUsed()
    {
        var netscope = Path.Combine(AppContext.BaseDirectory, "netscope.dll");
        var r = TestEnv.Run(netscope, "--il", "--filter", "NetScope.Reporting.TextSafety::EscapeControl", "--format", "json");
        using var doc = JsonDocument.Parse(r.Stdout);
        Assert.Equal("embedded", doc.RootElement.GetProperty("assemblies")[0].GetProperty("symbols").GetString());
        Assert.Contains(Method(doc).GetProperty("il").EnumerateArray(), i => i.TryGetProperty("source", out var s) && s.GetProperty("file").GetString() == "TextSafety.cs");
    }

    [Fact]
    public void NoPdbDisablesSymbols()
    {
        var r = TestEnv.Run(TestEnv.PdbSamplePath, "--il", "--cfg", "--no-pdb", "--filter", "*::SumOfSquares", "--no-color");
        Assert.DoesNotContain("Calculator.cs", r.Stdout);
        Assert.Contains("V_4", r.Stdout);
    }
}
