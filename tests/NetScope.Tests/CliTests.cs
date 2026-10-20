using System.Runtime.InteropServices;
using System.Text.Json;
using NetScope.Input;
using Xunit;

namespace NetScope.Tests;

public class CliTests
{
    [Fact]
    public void DefaultOutputListsMethods()
    {
        var r = TestEnv.Run(TestEnv.SamplesPath, "--no-color");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("Assembly : NetScope.Samples.dll", r.Stdout);
        Assert.Contains("Method   : bool Login(string user, string pass)", r.Stdout);
        Assert.Contains("Kind     : instance, public, virtual", r.Stdout);
        Assert.DoesNotContain("[IL]", r.Stdout);
    }

    [Fact]
    public void StdoutStaysCleanWhenFilesFail()
    {
        var dir = TestEnv.TempDir();
        var junk = Path.Combine(dir, "junk.dll");
        File.WriteAllBytes(junk, new byte[] { (byte)'M', (byte)'Z' }.Concat(Enumerable.Repeat((byte)7, 500)).ToArray());
        var r = TestEnv.Run(junk, TestEnv.SamplesPath, Path.Combine(dir, "missing.dll"), "--no-color");
        Assert.Equal(1, r.ExitCode); // partially processed
        Assert.Contains("Assembly : NetScope.Samples.dll", r.Stdout);
        Assert.DoesNotContain("netscope:", r.Stdout);
        Assert.Contains("junk.dll", r.Stderr);
        Assert.Contains("missing.dll", r.Stderr);
    }

    [Fact]
    public void ExitCodes()
    {
        var dir = TestEnv.TempDir();
        var text = Path.Combine(dir, "notes.txt");
        File.WriteAllText(text, "hello");
        Assert.Equal(3, TestEnv.Run(text).ExitCode);
        Assert.Equal(3, TestEnv.Run(Path.Combine(dir, "*.dll")).ExitCode);
        Assert.Equal(2, TestEnv.Run().ExitCode);
        Assert.Equal(2, TestEnv.Run(TestEnv.SamplesPath, "--format", "xml").ExitCode);
        Assert.Equal(2, TestEnv.Run(TestEnv.SamplesPath, "--type", "nonsense").ExitCode);
        Assert.Equal(2, TestEnv.Run(TestEnv.SamplesPath, "--no-such-option").ExitCode);
        Assert.Equal(0, TestEnv.Run("--version").ExitCode);
    }

    [Fact]
    public void JsonHasStableFields()
    {
        var r = TestEnv.Run(TestEnv.SamplesPath, "--all", "--format", "json", "--filter", "MyApp.Auth.LoginService::Login");
        Assert.Equal(0, r.ExitCode);
        using var doc = JsonDocument.Parse(r.Stdout);
        var asm = doc.RootElement.GetProperty("assemblies")[0];
        var m = Assert.Single(asm.GetProperty("methods").EnumerateArray());
        foreach (var field in new[] { "assembly", "type", "name", "signature", "kinds", "calls", "callers", "blocks", "pseudocode", "il", "native", "errors" })
            Assert.True(m.TryGetProperty(field, out _), "missing field " + field);
        Assert.Equal("Login", m.GetProperty("name").GetString());
        Assert.Equal(3, m.GetProperty("blocks").GetArrayLength());
        Assert.True(doc.RootElement.TryGetProperty("callgraph", out _));
    }

    [Fact]
    public void AbsentGroupsAreNotWritten()
    {
        var r = TestEnv.Run(TestEnv.SamplesPath, "--format", "json", "--filter", "*::Loop");
        using var doc = JsonDocument.Parse(r.Stdout);
        var m = doc.RootElement.GetProperty("assemblies")[0].GetProperty("methods")[0];
        Assert.False(m.TryGetProperty("il", out _));
        Assert.False(m.TryGetProperty("blocks", out _));
        Assert.False(m.TryGetProperty("pseudocode", out _));
    }

    [Fact]
    public void OutputIsDeterministic()
    {
        var a = TestEnv.Run(TestEnv.SamplesPath, "--all", "--no-color", "--threads", "4").Stdout;
        var b = TestEnv.Run(TestEnv.SamplesPath, "--all", "--no-color", "--threads", "1").Stdout;
        Assert.Equal(a, b);
    }

    [Fact]
    public void GroupKeepsArgumentOrderAndRemovesDuplicates()
    {
        var core = typeof(object).Assembly.Location;
        var r = TestEnv.Run(core, TestEnv.SamplesPath, core, "--filter", "*::Login", "--no-color");
        var first = r.Stdout.IndexOf("Assembly : System.Private.CoreLib.dll", StringComparison.Ordinal);
        var second = r.Stdout.IndexOf("Assembly : NetScope.Samples.dll", StringComparison.Ordinal);
        Assert.True(first >= 0 && second > first);
        Assert.Equal(1, CountOf(r.Stdout, "Assembly : System.Private.CoreLib.dll"));
    }

    [Fact]
    public void DirectoryAndListInputs()
    {
        var dir = TestEnv.TempDir();
        var sub = Directory.CreateDirectory(Path.Combine(dir, "sub")).FullName;
        File.Copy(TestEnv.SamplesPath, Path.Combine(sub, "a.dll"));
        File.Copy(TestEnv.SamplesPath, Path.Combine(dir, "b.dll"));
        File.WriteAllText(Path.Combine(dir, "c.txt"), "not an assembly");

        var flat = new InputResolver().Resolve(new[] { new InputItem(InputKind.Directory, dir) }, recursive: false);
        Assert.Equal(new[] { "b.dll" }, flat.Select(Path.GetFileName));
        var deep = new InputResolver().Resolve(new[] { new InputItem(InputKind.Directory, dir) }, recursive: true);
        Assert.Equal(2, deep.Count);

        var list = Path.Combine(dir, "files.txt");
        File.WriteAllLines(list, new[] { "# comment", "sub/a.dll", "", "b.dll", "sub/a.dll" });
        var fromList = new InputResolver().Resolve(new[] { new InputItem(InputKind.List, list) }, false);
        Assert.Equal(new[] { "a.dll", "b.dll" }, fromList.Select(Path.GetFileName));

        var masks = new InputResolver().Resolve(new[] { new InputItem(InputKind.Path, Path.Combine(dir, "**", "*.dll")) }, false);
        Assert.Equal(2, masks.Count);
    }

    [Fact]
    public void OutDirectoryWritesOneReportPerAssembly()
    {
        var dir = TestEnv.TempDir();
        var copy = Path.Combine(dir, "copy.dll");
        File.Copy(TestEnv.SamplesPath, copy);
        var outDir = Path.Combine(dir, "reports") + Path.DirectorySeparatorChar;
        var r = TestEnv.Run(TestEnv.SamplesPath, copy, "--format", "json", "--methods", "--callgraph", "--out", outDir);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("", r.Stdout);
        var files = Directory.GetFiles(outDir).Select(Path.GetFileName).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "NetScope.Samples.dll.json", "callgraph.json", "copy.dll.json" }, files);
        foreach (var f in Directory.GetFiles(outDir)) JsonDocument.Parse(File.ReadAllText(f)).Dispose();
    }

    [Fact]
    public void GraphFormats()
    {
        var dot = TestEnv.Run(TestEnv.SamplesPath, "--format", "dot", "--filter", "*::NestedTry").Stdout;
        Assert.StartsWith("digraph ", dot);
        Assert.Contains("subgraph cluster_", dot);
        Assert.Contains("-> exit", dot);
        var mermaid = TestEnv.Run(TestEnv.SamplesPath, "--format", "mermaid", "--callgraph").Stdout;
        Assert.Contains("```mermaid", mermaid);
        Assert.Contains("flowchart LR", mermaid);
    }

    /// <summary>Acceptance: without --allow-run no code of the analyzed assembly runs (its module initializer writes a marker).</summary>
    [Fact]
    public void AnalyzedCodeIsNeverExecutedWithoutAllowRun()
    {
        var marker = Path.Combine(TestEnv.TempDir(), "marker.txt");
        Environment.SetEnvironmentVariable("NETSCOPE_SAMPLE_MARKER", marker);
        try
        {
            var r = TestEnv.Run(TestEnv.SamplesPath, "--all", "--no-color");
            Assert.Equal(0, r.ExitCode);
            Assert.Contains("use --allow-run", r.Stdout);
            Assert.False(File.Exists(marker), "analyzed code was executed");
        }
        finally
        {
            Environment.SetEnvironmentVariable("NETSCOPE_SAMPLE_MARKER", null);
        }
    }

    [Fact]
    public void ReadyToRunCodeIsReadFromImage()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
        var core = typeof(object).Assembly.Location;
        var r = TestEnv.Run(core, "--native", "--filter", "System.String::Concat", "--no-color");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("x64, R2R", r.Stdout);
        Assert.Contains("ret", r.Stdout);
    }

    private static int CountOf(string s, string what)
    {
        int n = 0;
        for (int i = s.IndexOf(what, StringComparison.Ordinal); i >= 0; i = s.IndexOf(what, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }
}
