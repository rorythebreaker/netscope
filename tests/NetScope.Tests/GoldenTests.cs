using Xunit;

namespace NetScope.Tests;

/// <summary>
/// Reference output for the sample assembly. Regenerate after an intended change with
/// NETSCOPE_UPDATE_GOLDEN=1 dotnet test, then review the diff.
/// </summary>
public class GoldenTests
{
    [Theory]
    [InlineData("samples.methods.txt", "--methods")]
    [InlineData("samples.calls-cfg.txt", "--calls", "--cfg")]
    [InlineData("samples.il.txt", "--il")]
    [InlineData("samples.pseudo.txt", "--pseudo")]
    [InlineData("samples.uses.txt", "--uses", "MyApp.Db.Users::*", "--uses", "System.Linq.Enumerable")]
    [InlineData("samples.find-string.txt", "--find-string", "e")]
    [InlineData("samples.fields-strings.txt", "--fields", "--strings")]
    public void MatchesReference(string goldenFile, params string[] options)
    {
        var r = TestEnv.Run(new[] { TestEnv.SamplesPath, "--no-color" }.Concat(options).ToArray());
        Assert.Equal(0, r.ExitCode);
        var actual = Normalize(r.Stdout);
        var path = Path.Combine(TestEnv.GoldenDir, goldenFile);
        if (Environment.GetEnvironmentVariable("NETSCOPE_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(TestEnv.GoldenDir);
            File.WriteAllText(path, actual);
            return;
        }
        Assert.True(File.Exists(path), $"golden file {goldenFile} is missing; run with NETSCOPE_UPDATE_GOLDEN=1");
        Assert.Equal(Normalize(File.ReadAllText(path)), actual);
    }

    /// <summary>Removes machine-specific lines (absolute path) and normalizes line endings.</summary>
    private static string Normalize(string text) =>
        string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Where(l => !l.StartsWith("Path     :", StringComparison.Ordinal)));
}
