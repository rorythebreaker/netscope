using System.Text.Json;
using NetScope.Ghidra;
using NetScope.Loading;
using NetScope.Pipeline;
using Xunit;

namespace NetScope.Tests;

/// <summary>
/// The Ghidra bridge (TZ-NetScope-2 §5): a pure static-analysis export that names Ghidra's unnamed native
/// functions. Verifies the §5.5 acceptance criteria — the script's addresses match `--native --format json`,
/// the named-function count matches the native methods, and the script text is free of network/external
/// access — plus idempotency, escaping and the pure-IL warning.
/// </summary>
public sealed class GhidraTests : IDisposable
{
    private readonly string _dir = TestEnv.TempDir();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static readonly string[] ForbiddenTokens =
    {
        "import socket", "import os", "import subprocess", "import urllib", "import requests",
        "open(", "exec(", "eval(", "os.system", "Runtime.getRuntime", "ProcessBuilder",
        "java.net", "java.io.File", "URL(", "Socket(", "http://", "https://",
    };

    [Fact]
    public void ScriptAddressesMatchNativeJson()
    {
        var script = Path.Combine(_dir, "annotate.py");
        var r = TestEnv.Run(ExportGhidraCommand.Name, TestEnv.SamplesR2RPath, "--out", script, "-q");
        Assert.Equal(Analyzer.ExitOk, r.ExitCode);

        var scriptAddrs = ReadIntArgs(File.ReadAllText(script), "_name_function")
            .Concat(ReadIntArgs(File.ReadAllText(script), "_label"))
            .ToHashSet();

        // §5.5: addresses must equal the RVAs from --native --format json.
        var json = Path.Combine(_dir, "native.json");
        var n = TestEnv.Run(TestEnv.SamplesR2RPath, "--native", "--format", "json", "--out", json, "-q");
        Assert.Equal(Analyzer.ExitOk, n.ExitCode);
        var nativeAddrs = NativePartAddresses(json);

        Assert.NotEmpty(nativeAddrs);
        Assert.Equal(nativeAddrs, scriptAddrs);
    }

    [Fact]
    public void NamedFunctionCountMatchesNativeMethods()
    {
        using var asm = new AssemblyLoader().TryLoad(0, TestEnv.SamplesR2RPath, out _)!;
        var ann = GhidraAnnotationBuilder.Build(asm!, audit: false);

        // One named function per native main body; funclets are labels, not named functions.
        Assert.NotEmpty(ann.Functions);
        Assert.All(ann.Functions, f => Assert.True(f.Rva > 0));
        Assert.Equal(ann.Functions.Select(f => f.Rva).Distinct().Count(), ann.Functions.Count);
    }

    [Theory]
    [InlineData("python")]
    [InlineData("java")]
    public void ScriptHasNoNetworkOrExternalAccess(string lang)
    {
        using var asm = new AssemblyLoader().TryLoad(0, TestEnv.SamplesR2RPath, out _)!;
        var ann = GhidraAnnotationBuilder.Build(asm!, audit: true);
        var text = GhidraScriptWriter.Write(ann, lang);

        foreach (var token in ForbiddenTokens)
            Assert.False(text.Contains(token, StringComparison.OrdinalIgnoreCase), $"script ({lang}) must not contain '{token}'");
    }

    [Fact]
    public void ScriptIsAsciiOnly()
    {
        // Non-ASCII (e.g. obfuscated Unicode names) must be escaped to \uXXXX so the file stays ASCII.
        using var asm = new AssemblyLoader().TryLoad(0, TestEnv.SamplesR2RPath, out _)!;
        var text = GhidraScriptWriter.Write(GhidraAnnotationBuilder.Build(asm!, audit: true), "python");
        Assert.All(text, c => Assert.True(c <= 0x7e, $"unescaped non-ASCII char U+{(int)c:X4}"));
    }

    [Fact]
    public void OutputIsDeterministicAndIdempotentlyWritten()
    {
        using var asm = new AssemblyLoader().TryLoad(0, TestEnv.SamplesR2RPath, out _)!;
        var a = GhidraScriptWriter.Write(GhidraAnnotationBuilder.Build(asm!, audit: true), "python");
        var b = GhidraScriptWriter.Write(GhidraAnnotationBuilder.Build(asm!, audit: true), "python");
        Assert.Equal(a, b); // deterministic

        // Idempotent application: updates names in place and clears a prior run's bookmarks.
        Assert.Contains("setName(name, SourceType.USER_DEFINED)", a);
        Assert.Contains("removeBookmark", a);
    }

    [Fact]
    public void JavaOutputIsAGhidraScriptClass()
    {
        using var asm = new AssemblyLoader().TryLoad(0, TestEnv.SamplesR2RPath, out _)!;
        var java = GhidraScriptWriter.Write(GhidraAnnotationBuilder.Build(asm!, audit: false), "java");
        Assert.Contains("extends GhidraScript", java);
        Assert.Contains("public void run()", java);
    }

    [Fact]
    public void PureIlAssemblyWarnsNothingToAnnotate()
    {
        var script = Path.Combine(_dir, "il.py");
        var r = TestEnv.Run(ExportGhidraCommand.Name, TestEnv.SamplesPath, "--out", script);
        Assert.Equal(Analyzer.ExitOk, r.ExitCode);
        Assert.Contains("nothing to annotate", r.Stderr);
        Assert.True(File.Exists(script)); // a script is still written
    }

    [Fact]
    public void OutIsRequired()
    {
        var r = TestEnv.Run(ExportGhidraCommand.Name, TestEnv.SamplesR2RPath, "-q");
        Assert.Equal(Analyzer.ExitBadArguments, r.ExitCode);
    }

    [Fact]
    public void AuditAddsBookmarks()
    {
        using var asm = new AssemblyLoader().TryLoad(0, TestEnv.SamplesR2RPath, out _)!;
        var withAudit = GhidraAnnotationBuilder.Build(asm!, audit: true);
        var without = GhidraAnnotationBuilder.Build(asm!, audit: false);
        Assert.Empty(without.Bookmarks);
        Assert.NotEmpty(withAudit.Bookmarks);
    }

    private static GhidraAnnotations Fabricated(params (int Rva, string Name, string? Signature)[] functions)
    {
        var ann = new GhidraAnnotations { SourceFile = "x.dll", AssemblyName = "x", Architecture = "x64" };
        foreach (var f in functions) ann.Functions.Add(new GhidraFunction(f.Rva, f.Name, f.Signature));
        return ann;
    }

    /// <summary>Regression: Ghidra rejects whitespace in symbol names; "A&lt;char, char&gt;" aborted the whole script.</summary>
    [Theory]
    [InlineData("python")]
    [InlineData("java")]
    public void SymbolNamesHaveNoWhitespaceButCommentsKeepTheSignature(string lang)
    {
        var text = GhidraScriptWriter.Write(Fabricated((0x1000, "SelectEnumerableIterator<char, char>::ToArray", "TResult[] ToArray()")), lang);
        Assert.Contains("\"SelectEnumerableIterator<char,_char>::ToArray\"", text);
        Assert.DoesNotContain("<char, char>", text);
        Assert.Contains("\"TResult[] ToArray()\"", text); // the plate comment is not a symbol
        Assert.Equal("a_b_c", GhidraScriptWriter.SymbolName("a b\tc"));
    }

    /// <summary>Regression: without the u prefix Jython (Ghidra's default Python) kept "С" as six literal characters.</summary>
    [Fact]
    public void PythonStringsAreUnicodeLiterals()
    {
        var text = GhidraScriptWriter.Write(Fabricated((0x1000, "Счёт", null)), "python");
        Assert.Contains("_name_function(4096, u\"\\u0421\\u0447\\u0451\\u0442\", None)", text);
    }

    /// <summary>TZ-3 §4.2: Ghidra's PE loader does not recognize an R2R image for Linux (OS-encoded Machine). The
    /// export warns and names the analyzeHeadless options that import the unmodified file (checked in CI).</summary>
    [Theory]
    [InlineData("19-Library-R2R-linux-arm64", "Linux", "-loader PeLoader -processor AARCH64:LE:64:v8A -cspec default")]
    [InlineData("18-Library-R2R-win-x64", null, null)]
    public void ReadyToRunForAnotherOsNamesTheGhidraImportOptions(string id, string? os, string? options)
    {
        var script = Path.Combine(_dir, "annotate.py");
        var r = TestEnv.Run(ExportGhidraCommand.Name, ReferenceSet.Get(id).Path, "--out", script);
        Assert.Equal(Analyzer.ExitOk, r.ExitCode);
        var text = File.ReadAllText(script);
        if (os == null)
        {
            Assert.DoesNotContain(GhidraScriptWriter.ImportOptionsLabel, text);
            Assert.DoesNotContain("OS-encoded", r.Stderr);
            return;
        }
        Assert.Contains("# " + GhidraScriptWriter.ImportOptionsLabel + options, text);
        Assert.Contains($"(ReadyToRun for {os})", text);
        Assert.Contains($"{os} ReadyToRun image", r.Stderr);
        Assert.Contains(options!, r.Stderr);
    }

    [Theory]
    [InlineData((ushort)0x8664, "Windows")]
    [InlineData((ushort)(0xAA64 ^ 0x7B79), "Linux")]
    [InlineData((ushort)(0x8664 ^ 0x7B79), "Linux")]
    [InlineData((ushort)(0xAA64 ^ 0x4644), "macOS")]
    [InlineData((ushort)(0x8664 ^ 0xADC4), "FreeBSD")]
    public void ReadyToRunTargetOsIsDecodedFromTheMachineField(ushort machine, string os) =>
        Assert.Equal(os, Native.R2RReader.TargetOs((System.Reflection.PortableExecutable.Machine)machine));

    /// <summary>TZ-3 §4.1: Ghidrathon exposes the script state as functions (currentProgram()), Jython as variables.
    /// The script must use neither form directly, and must report the interpreter it ran in.</summary>
    [Fact]
    public void PythonScriptRunsInJythonAndGhidrathon()
    {
        var text = GhidraScriptWriter.Write(Fabricated((0x1000, "M", null)), "python");
        Assert.Contains("_program = currentProgram() if callable(currentProgram) else currentProgram", text);
        Assert.DoesNotContain("currentProgram.", text);
        Assert.Contains("[%s]' % _python", text);
        Assert.Contains("Ghidrathon " + GhidraScriptWriter.GhidrathonVersion, text);
    }

    /// <summary>Regression: GhidraScript needs the public class to match the file name ("Annotate.java" did not compile).</summary>
    [Fact]
    public void JavaClassIsNamedAfterTheOutputFile()
    {
        var script = Path.Combine(_dir, "Annotate.java");
        var r = TestEnv.Run(ExportGhidraCommand.Name, TestEnv.SamplesR2RPath, "--out", script, "--lang", "java", "-q");
        Assert.Equal(Analyzer.ExitOk, r.ExitCode);
        Assert.Contains("public class Annotate extends GhidraScript", File.ReadAllText(script));
    }

    [Theory]
    [InlineData("my-script.java")]
    [InlineData("1annotate.java")]
    [InlineData("class.java")]
    public void JavaFileNameThatCannotBeAClassIsRejected(string fileName)
    {
        var script = Path.Combine(_dir, fileName);
        var r = TestEnv.Run(ExportGhidraCommand.Name, TestEnv.SamplesR2RPath, "--out", script, "--lang", "java");
        Assert.Equal(Analyzer.ExitBadArguments, r.ExitCode);
        Assert.Contains("valid Java class name", r.Stderr);
        Assert.False(File.Exists(script));
    }

    /// <summary>One entry that Ghidra rejects is counted and skipped instead of aborting the script.</summary>
    [Theory]
    [InlineData("python", "except:", "_failed.append(rva)")]
    [InlineData("java", "catch (Exception e)", "fail(rva);")]
    public void EntriesFailIndependently(string lang, string handler, string record)
    {
        var text = GhidraScriptWriter.Write(Fabricated((0x1000, "A", null)), lang);
        Assert.Contains(handler, text);
        Assert.Contains(record, text);
        Assert.Contains("failed", text);
    }

    // --- helpers ---

    private static List<int> ReadIntArgs(string script, string func)
    {
        var result = new List<int>();
        foreach (var line in script.Split('\n'))
        {
            var t = line.TrimStart();
            if (!t.StartsWith(func + "(", StringComparison.Ordinal)) continue;
            int open = t.IndexOf('(');
            int comma = t.IndexOf(',', open);
            if (comma > open && int.TryParse(t[(open + 1)..comma].Trim(), out int rva)) result.Add(rva);
        }
        return result;
    }

    private static HashSet<int> NativePartAddresses(string jsonFile)
    {
        var addrs = new HashSet<int>();
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonFile));
        void Walk(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                if (e.TryGetProperty("nativeInfo", out var ni) && ni.TryGetProperty("parts", out var parts))
                    foreach (var p in parts.EnumerateArray())
                        addrs.Add(p.GetProperty("address").GetInt32());
                foreach (var prop in e.EnumerateObject()) Walk(prop.Value);
            }
            else if (e.ValueKind == JsonValueKind.Array)
                foreach (var item in e.EnumerateArray()) Walk(item);
        }
        Walk(doc.RootElement);
        return addrs;
    }
}
