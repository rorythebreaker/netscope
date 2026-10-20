using System.Text.Json;
using NetScope.Obfuscation;
using Xunit;

namespace NetScope.Tests;

public class ObfuscationTests
{
    private static JsonElement Verdict(string path)
    {
        var r = TestEnv.Run(path, "--detect-obfuscation", "--format", "json");
        Assert.Equal(0, r.ExitCode);
        using var doc = JsonDocument.Parse(r.Stdout);
        var asm = doc.RootElement.GetProperty("assemblies")[0];
        Assert.Equal(0, asm.GetProperty("methods").GetArrayLength()); // only the verdict is reported
        return asm.GetProperty("obfuscation").Clone();
    }

    /// <summary>Acceptance 1.1: "yes" for ConfuserEx output, with and without its watermark attribute.</summary>
    [Fact]
    public void ConfuserExOutputIsDetected()
    {
        var marked = Verdict(TestEnv.ObfuscatedFixture("confuserex-normal.exe"));
        Assert.Equal("yes", marked.GetProperty("verdict").GetString());
        Assert.Equal("ConfuserEx", marked.GetProperty("obfuscator").GetString());
        Assert.Contains("de4dot", marked.GetProperty("hint").GetString());

        var unmarked = Verdict(TestEnv.ObfuscatedFixture("confuserex-nowatermark.exe"));
        Assert.Equal("yes", unmarked.GetProperty("verdict").GetString());
        Assert.False(unmarked.TryGetProperty("obfuscator", out _));
        var kinds = unmarked.GetProperty("signs").EnumerateArray().Select(s => s.GetProperty("kind").GetString()).ToList();
        Assert.Contains("names", kinds);
        Assert.Contains("control-flow", kinds);
    }

    /// <summary>Acceptance 1.1: "no" for every test assembly that is not obfuscated.</summary>
    [Fact]
    public void PlainAssembliesAreNotObfuscated()
    {
        var plain = new[]
        {
            TestEnv.SamplesPath, TestEnv.SamplesR2RPath, TestEnv.PdbSamplePath, TestEnv.HostilePath,
            TestEnv.ObfuscatedFixture("plain.exe"),
            Path.Combine(AppContext.BaseDirectory, "netscope.dll"),
            typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
            typeof(object).Assembly.Location,
        };
        foreach (var path in plain)
        {
            var v = Verdict(path);
            Assert.True(v.GetProperty("verdict").GetString() == "no", path + ": " + v);
            Assert.False(v.TryGetProperty("hint", out _));
        }
    }

    [Theory]
    [InlineData("a", true)]
    [InlineData("Login", false)]
    [InlineData("Привет", false)]
    [InlineData("‮x‬", true)]
    [InlineData("ab​cd", true)]
    [InlineData("", true)]
    public void UnreadableNames(string name, bool unreadable) => Assert.Equal(unreadable, ObfuscationDetector.IsUnreadable(name));

    [Fact]
    public void TextOutputHasVerdictSignsAndHint()
    {
        var r = TestEnv.Run(TestEnv.ObfuscatedFixture("confuserex-normal.exe"), "--detect-obfuscation", "--no-color");
        Assert.Contains("Obfuscated: yes (ConfuserEx)", r.Stdout);
        Assert.Contains("  sign: attribute: ConfusedByAttribute", r.Stdout);
        Assert.Contains("  hint: deobfuscate the file with an external tool such as de4dot", r.Stdout);
        Assert.DoesNotContain("Method   :", r.Stdout);
    }
}
