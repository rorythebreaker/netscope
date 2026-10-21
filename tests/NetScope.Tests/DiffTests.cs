using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NetScope.Diff;
using Xunit;

namespace NetScope.Tests;

public class DiffTests
{
    private const string Library = """
        using System;
        using System.Collections.Generic;
        using System.Diagnostics;

        namespace Shop
        {
            //SHIFTED
            public class Cart
            {
                private readonly List<decimal> _prices = new();
                public void Add(decimal price) { if (price < 0) throw new ArgumentOutOfRangeException(nameof(price)); _prices.Add(price); }
                public decimal Total()
                {
                    decimal sum = 0;
                    foreach (var p in _prices) sum += p;
                    return sum; //CHANGED
                }
                public int Count => _prices.Count;
                public string Describe(int n) => n switch { 0 => "empty", 1 => "one", _ => "many" };
            }

            public static class Tools
            {
                public static string Greet(string name) => "Hello, " + name;
                public static int Twice(int x) => x * 2;
            }
        }
        """;

    private static string Compile(string source, string name, bool deterministic = true)
    {
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p) is "System.Private.CoreLib.dll" or "System.Runtime.dll" or "System.Collections.dll" or "System.Diagnostics.Process.dll" or "System.ComponentModel.Primitives.dll" or "System.Console.dll")
            .Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("Shop", new[] { CSharpSyntaxTree.ParseText(source) }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, deterministic: deterministic));
        var dir = Path.Combine(TestEnv.TempDir(), name);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Shop.dll");
        var emit = compilation.Emit(path);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        return path;
    }

    private static JsonElement Diff(string a, string b, params string[] options)
    {
        var r = TestEnv.Run(new[] { "diff", a, b, "--format", "json" }.Concat(options).ToArray());
        Assert.True(r.ExitCode is 0 or 5, r.Stderr);
        using var doc = JsonDocument.Parse(r.Stdout);
        return doc.RootElement.Clone();
    }

    /// <summary>Acceptance 1.1: the same code built twice (new MVID) has no changes.</summary>
    [Fact]
    public void RebuildOfTheSameCodeHasNoChanges()
    {
        var first = Compile(Library, "a", deterministic: false);
        var second = Compile(Library, "b", deterministic: false);
        Assert.NotEqual(File.ReadAllBytes(first), File.ReadAllBytes(second)); // really two builds
        var d = Diff(first, second);
        Assert.False(d.GetProperty("changed").GetBoolean(), d.ToString());
        var summary = d.GetProperty("assemblies")[0].GetProperty("summary");
        Assert.Equal(0, summary.GetProperty("changed").GetInt32());
        Assert.True(summary.GetProperty("unchanged").GetInt32() > 5);
    }

    /// <summary>Acceptance 1.1: changing one method gives exactly one changed method.</summary>
    [Fact]
    public void OneChangedMethodIsReported()
    {
        var before = Compile(Library, "before");
        var after = Compile(Library.Replace("return sum; //CHANGED", "Console.WriteLine(sum); return sum > 100 ? sum * 0.9m : sum;"), "after");
        var asm = Diff(before, after).GetProperty("assemblies")[0];
        var changed = Assert.Single(asm.GetProperty("methods").EnumerateArray());
        Assert.Equal("changed", changed.GetProperty("status").GetString());
        Assert.Equal("Shop.Cart::Total", changed.GetProperty("method").GetString());
        var added = changed.GetProperty("callsAdded").EnumerateArray().Select(c => c.GetString()).ToList();
        Assert.True(added.Contains("System.Console::WriteLine(Decimal)"), string.Join(" | ", added));
        Assert.True(changed.GetProperty("complexity").GetProperty("new").GetInt32() > changed.GetProperty("complexity").GetProperty("old").GetInt32());
        var il = changed.GetProperty("ilDiff").GetString()!;
        Assert.StartsWith("--- old/Shop.Cart::Total\n+++ new/Shop.Cart::Total\n@@ ", il);
        Assert.Contains("\n+IL_", il);
        Assert.Equal(1, asm.GetProperty("summary").GetProperty("changed").GetInt32());
    }

    /// <summary>Tokens of every method change when a type is inserted first; matching by name keeps them unchanged.</summary>
    [Fact]
    public void InsertedTypeShiftsTokensButChangesNothingElse()
    {
        var before = Compile(Library, "before");
        var after = Compile(Library.Replace("//SHIFTED", "public class Coupon { public string Code = \"\"; public decimal Apply(decimal v) => v - 1; }"), "after");
        var asm = Diff(before, after).GetProperty("assemblies")[0];
        var methods = asm.GetProperty("methods").EnumerateArray().ToList();
        Assert.All(methods, m => Assert.Equal("added", m.GetProperty("status").GetString()));
        Assert.Contains(methods, m => m.GetProperty("method").GetString() == "Shop.Coupon::Apply");
        Assert.Equal(0, asm.GetProperty("summary").GetProperty("changed").GetInt32());
        var metadata = asm.GetProperty("metadata").EnumerateArray().Select(m => m.GetProperty("kind").GetString() + " " + m.GetProperty("change").GetString() + " " + m.GetProperty("item").GetString()).ToList();
        Assert.Contains("type added Shop.Coupon", metadata);
        Assert.Contains("field added Shop.Coupon::Code : string", metadata);
    }

    [Fact]
    public void ExitCodeLikeGitDiff()
    {
        var a = Compile(Library, "a");
        var b = Compile(Library.Replace("x * 2", "x * 3"), "b");
        Assert.Equal(0, TestEnv.Run("diff", a, a, "--exit-code").ExitCode);
        Assert.Equal(5, TestEnv.Run("diff", a, b, "--exit-code").ExitCode);
        Assert.Equal(0, TestEnv.Run("diff", a, b).ExitCode);
        Assert.Equal(2, TestEnv.Run("diff", a).ExitCode);
        Assert.Equal(3, TestEnv.Run("diff", a, Path.Combine(TestEnv.TempDir(), "missing.dll")).ExitCode);
    }

    [Fact]
    public void PseudocodeDiff()
    {
        var a = Compile(Library, "a");
        var b = Compile(Library.Replace("\"Hello, \" + name", "\"Hi, \" + name.Trim()"), "b");
        var m = Assert.Single(Diff(a, b, "--pseudo").GetProperty("assemblies")[0].GetProperty("methods").EnumerateArray());
        var pseudo = m.GetProperty("pseudocodeDiff").GetString()!;
        Assert.Contains("-    return \"Hello, \" + name;", pseudo);
        Assert.Contains("+    return \"Hi, \" + name.Trim();", pseudo);
    }

    [Fact]
    public void DirectoriesAreMatchedByAssemblyName()
    {
        var oldDir = Path.GetDirectoryName(Compile(Library, "old"))!;
        var newDir = Path.GetDirectoryName(Compile(Library.Replace("x * 2", "x + x + 1"), "new"))!;
        File.Copy(TestEnv.PdbSamplePath, Path.Combine(oldDir, "Gone.dll"));
        File.Copy(TestEnv.HostilePath, Path.Combine(newDir, "Fresh.dll"));
        File.WriteAllText(Path.Combine(newDir, "native.dll"), "not managed");
        var r = TestEnv.Run("diff", oldDir, newDir, "--no-color");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("Removed  : " + Path.Combine(oldDir, "Gone.dll"), r.Stdout);
        Assert.Contains("Added    : " + Path.Combine(newDir, "Fresh.dll"), r.Stdout);
        Assert.Contains("[Changed] Shop.Tools::Twice", r.Stdout);
    }

    /// <summary>With --audit only findings introduced by the new version are shown.</summary>
    [Fact]
    public void AuditShowsOnlyNewFindings()
    {
        var a = Compile(Library.Replace("//SHIFTED", "public static class Old { public static void Run() => Process.Start(\"a.exe\"); }"), "a");
        var b = Compile(Library.Replace("//SHIFTED", "public static class Old { public static void Run() => Process.Start(\"a.exe\"); }\npublic static class New { public static void Run() => Process.Start(\"b.exe\"); }"), "b");
        var findings = Diff(a, b, "--audit").GetProperty("assemblies")[0].GetProperty("newFindings").EnumerateArray().ToList();
        var f = Assert.Single(findings);
        Assert.Equal("NS001", f.GetProperty("rule").GetString());
        Assert.Equal("Shop.New::Run", f.GetProperty("method").GetString());
    }

    [Fact]
    public void UnifiedDiffFormat()
    {
        var a = new[] { "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k" };
        var b = new[] { "a", "b", "c", "X", "e", "f", "g", "h", "i", "j", "k", "l" };
        // Seven unchanged lines between the changes (more than 2 x 3 of context): two hunks.
        var text = LineDiff.Unified(a, b, LineDiff.Compute(a, b), "old", "new");
        Assert.Equal("--- old\n+++ new\n@@ -1,7 +1,7 @@\n a\n b\n c\n-d\n+X\n e\n f\n g\n@@ -9,3 +9,4 @@\n i\n j\n k\n+l\n", text);
        // Six: one hunk, as in GNU diff.
        var c = b.Take(11).ToArray();
        var merged = LineDiff.Unified(a.Take(10).ToArray(), c, LineDiff.Compute(a.Take(10).ToArray(), c), "old", "new");
        Assert.StartsWith("--- old\n+++ new\n@@ -1,10 +1,11 @@\n", merged);
        Assert.Equal("", LineDiff.Unified(a, a, LineDiff.Compute(a, a), "old", "new"));
    }

    [Fact]
    public void HiddenCharactersInNamesAreEscaped()
    {
        Assert.Equal("a\\u202Eb", Reporting.TextSafety.EscapeName("a\u202Eb"));
        Assert.Equal("Обычное имя", Reporting.TextSafety.EscapeName("Обычное имя"));
    }
}
