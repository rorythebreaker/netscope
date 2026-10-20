using System.Text.Json;
using NetScope.Audit;
using NJsonSchema;
using Xunit;

namespace NetScope.Tests;

public class AuditTests
{
    private sealed record Hit(string Type, string Method, string Rule, string Level);

    private static List<Hit> Findings(string path, params string[] options)
    {
        var r = TestEnv.Run(new[] { path, "--audit", "--format", "json" }.Concat(options).ToArray());
        Assert.True(r.ExitCode is 0 or 4, r.Stderr);
        using var doc = JsonDocument.Parse(r.Stdout);
        var hits = new List<Hit>();
        foreach (var m in doc.RootElement.GetProperty("assemblies")[0].GetProperty("methods").EnumerateArray())
            foreach (var f in m.GetProperty("findings").EnumerateArray())
                hits.Add(new Hit(m.GetProperty("type").GetString()!, m.GetProperty("name").GetString()!, f.GetProperty("rule").GetString()!, f.GetProperty("level").GetString()!));
        return hits;
    }

    /// <summary>Acceptance 1.1: each rule has a positive and a negative example in NetScope.Samples.</summary>
    [Fact]
    public void EveryRuleFindsItsPositiveExamplesAndNothingElse()
    {
        var hits = Findings(TestEnv.SamplesPath);
        var (_, _, methods) = TestEnv.LoadSamples();
        foreach (var rule in RuleSet.All)
        {
            var type = string.Concat("MyApp.Audit.Ns", rule.Id.AsSpan(2));
            var positives = methods.Where(m => m.TypeName == type && m.Name.StartsWith("Positive", StringComparison.Ordinal)).Select(m => m.Name).ToList();
            var negatives = methods.Where(m => m.TypeName == type && m.Name.StartsWith("Negative", StringComparison.Ordinal)).Select(m => m.Name).ToList();
            Assert.True(negatives.Count > 0, $"{rule.Id} has no negative example");
            if (rule.Id != "NS008") Assert.True(positives.Count > 0, $"{rule.Id} has no positive example");
            foreach (var p in positives)
                Assert.Contains(hits, h => h.Type == type && h.Method == p && h.Rule == rule.Id);
            foreach (var n in negatives)
                Assert.DoesNotContain(hits, h => h.Type == type && h.Method == n);
            Assert.All(hits.Where(h => h.Rule == rule.Id), h => Assert.Equal(rule.Level.ToString().ToLowerInvariant(), h.Level));
        }
        // P/Invoke declarations are findings of NS008 even without a body.
        Assert.Contains(hits, h => h.Method == "VirtualAlloc" && h.Rule == "NS008");
        Assert.Contains(hits, h => h.Method == "LoadLibrary" && h.Rule == "NS008");
        Assert.DoesNotContain(hits, h => h.Method == "GetCurrentThreadId");
        // Nothing outside the examples: the rest of the samples is ordinary code.
        Assert.All(hits, h => Assert.StartsWith("MyApp.Audit.Ns", h.Type));
    }

    [Fact]
    public void SecretsAreNotPrinted()
    {
        var r = TestEnv.Run(TestEnv.SamplesPath, "--audit", "--rules", "NS007", "--no-color");
        Assert.Contains("NS007", r.Stdout);
        Assert.DoesNotContain("Example-1", r.Stdout);
        Assert.DoesNotContain("EXAMPLEEXAMPLE12", r.Stdout);
    }

    [Fact]
    public void SuppressMessageAttributeHidesTheFinding()
    {
        var r = TestEnv.Run(TestEnv.SamplesPath, "--audit", "--rules", "NS001", "--format", "json");
        using var doc = JsonDocument.Parse(r.Stdout);
        var asm = doc.RootElement.GetProperty("assemblies")[0];
        Assert.DoesNotContain(asm.GetProperty("methods").EnumerateArray(), m => m.GetProperty("name").GetString() == "StartsToolOnPurpose");
        Assert.Equal(1, asm.GetProperty("audit").GetProperty("suppressed").GetInt32());
    }

    [Fact]
    public void SuppressionFileNextToTheAssembly()
    {
        var dir = TestEnv.TempDir();
        var copy = Path.Combine(dir, "NetScope.Samples.dll");
        File.Copy(TestEnv.SamplesPath, copy);
        File.WriteAllLines(Path.Combine(dir, Suppressions.FileName), new[] { "# legacy code", "NS004 MyApp.Audit.Ns004::*", "NS007 *::PositiveAwsKey" });
        var hits = Findings(copy);
        Assert.DoesNotContain(hits, h => h.Rule == "NS004");
        Assert.DoesNotContain(hits, h => h.Method == "PositiveAwsKey");
        Assert.Contains(hits, h => h.Method == "PositiveConnectionString");
    }

    [Fact]
    public void RulesAndMinLevelSelectFindings()
    {
        var only = Findings(TestEnv.SamplesPath, "--rules", "NS001,NS004");
        Assert.Equal(new[] { "NS001", "NS004" }, only.Select(h => h.Rule).Distinct().Order(StringComparer.Ordinal));
        var warnings = Findings(TestEnv.SamplesPath, "--min-level", "warning");
        Assert.DoesNotContain(warnings, h => h.Level == "note");
        Assert.Contains(warnings, h => h.Level == "error");
    }

    [Fact]
    public void FailOnErrorReturnsFour()
    {
        Assert.Equal(4, TestEnv.Run(TestEnv.SamplesPath, "--audit", "--fail-on", "error").ExitCode);
        Assert.Equal(0, TestEnv.Run(TestEnv.SamplesPath, "--audit").ExitCode);
        // Only warnings remain: below the threshold.
        Assert.Equal(0, TestEnv.Run(TestEnv.SamplesPath, "--audit", "--rules", "NS001", "--fail-on", "error").ExitCode);
        Assert.Equal(4, TestEnv.Run(TestEnv.SamplesPath, "--audit", "--rules", "NS001", "--fail-on", "warning").ExitCode);
    }

    [Fact]
    public void BadAuditArguments()
    {
        Assert.Equal(2, TestEnv.Run(TestEnv.SamplesPath, "--audit", "--rules", "NS999").ExitCode);
        Assert.Equal(2, TestEnv.Run(TestEnv.SamplesPath, "--audit", "--fail-on", "fatal").ExitCode);
        Assert.Equal(2, TestEnv.Run(TestEnv.SamplesPath, "--fail-on", "error").ExitCode);
    }

    [Fact]
    public void RulesAreDiscoveredAndDocumented()
    {
        Assert.Equal(Enumerable.Range(1, 10).Select(i => $"NS{i:000}"), RuleSet.All.Select(r => r.Id));
        Assert.All(RuleSet.All, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Title));
            Assert.False(string.IsNullOrWhiteSpace(r.Description));
            Assert.Matches("^[A-Z][A-Za-z]+$", r.Name);
        });
    }

    /// <summary>Acceptance 1.1: SARIF validates against the SARIF 2.1.0 schema.</summary>
    [Fact]
    public async Task SarifIsValidAgainstTheSchema()
    {
        var r = TestEnv.Run(TestEnv.SamplesPath, TestEnv.PdbSamplePath, "--format", "sarif");
        Assert.Equal(0, r.ExitCode);
        var schema = await JsonSchema.FromJsonAsync(File.ReadAllText(Path.Combine(TestEnv.RepoRoot, "tests", "NetScope.Tests", "Schemas", "sarif-schema-2.1.0.json")));
        var errors = schema.Validate(r.Stdout);
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors.Select(e => $"{e.Path}: {e.Kind}")));

        using var doc = JsonDocument.Parse(r.Stdout);
        var run = Assert.Single(doc.RootElement.GetProperty("runs").EnumerateArray());
        var rules = run.GetProperty("tool").GetProperty("driver").GetProperty("rules").EnumerateArray().Select(x => x.GetProperty("id").GetString()).ToList();
        Assert.Equal(RuleSet.All.Select(x => x.Id), rules);
        var results = run.GetProperty("results").EnumerateArray().ToList();
        Assert.NotEmpty(results);
        foreach (var result in results)
        {
            var props = result.GetProperty("properties");
            Assert.Contains(props.GetProperty("assembly").GetString(), new[] { "NetScope.Samples.dll", "NetScope.Samples.Pdb.dll" });
            Assert.Contains("::", props.GetProperty("method").GetString());
            var location = result.GetProperty("locations")[0];
            Assert.Equal(props.GetProperty("method").GetString(), location.GetProperty("logicalLocations")[0].GetProperty("fullyQualifiedName").GetString());
            Assert.Equal(rules.IndexOf(result.GetProperty("ruleId").GetString()), result.GetProperty("ruleIndex").GetInt32());
        }
        Assert.Contains(results, x => x.GetProperty("properties").TryGetProperty("ilOffset", out _));
        // Fingerprints are unique, so Code Scanning does not merge different findings.
        var prints = results.Select(x => x.GetProperty("partialFingerprints").GetProperty("netscope/v1").GetString()).ToList();
        Assert.Equal(prints.Count, prints.Distinct().Count());
    }

    [Fact]
    public void SarifUsesTheSourceLineFromThePdb()
    {
        var r = TestEnv.Run(TestEnv.PdbSamplePath, "--format", "sarif");
        using var doc = JsonDocument.Parse(r.Stdout);
        var result = Assert.Single(doc.RootElement.GetProperty("runs")[0].GetProperty("results").EnumerateArray());
        Assert.Equal("NS005", result.GetProperty("ruleId").GetString());
        var physical = result.GetProperty("locations")[0].GetProperty("physicalLocation");
        Assert.EndsWith("/tests/NetScope.Samples.Pdb/Calculator.cs", physical.GetProperty("artifactLocation").GetProperty("uri").GetString());
        var source = File.ReadAllLines(Path.Combine(TestEnv.RepoRoot, "tests", "NetScope.Samples.Pdb", "Calculator.cs"));
        int line = Array.FindIndex(source, l => l.Contains("MD5.HashData", StringComparison.Ordinal)) + 1;
        Assert.Equal(line, physical.GetProperty("region").GetProperty("startLine").GetInt32());
        // The assembly stays attached as a related location.
        Assert.EndsWith("NetScope.Samples.Pdb.dll", result.GetProperty("relatedLocations")[0].GetProperty("physicalLocation").GetProperty("artifactLocation").GetProperty("uri").GetString());
    }

    [Fact]
    public void SarifUrisAreRelativeUnderTheBaseDirectory()
    {
        var reporter = new Reporting.SarifReporter(RuleSet.All, TestEnv.RepoRoot);
        Assert.Equal("tests/NetScope.Samples/Audit.cs", reporter.Uri(Path.Combine(TestEnv.RepoRoot, "tests", "NetScope.Samples", "Audit.cs")));
        Assert.StartsWith("file:///", reporter.Uri(Path.Combine(Path.GetPathRoot(TestEnv.RepoRoot)!, "elsewhere", "a.cs")));
    }
}
