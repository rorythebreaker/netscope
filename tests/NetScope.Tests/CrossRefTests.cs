using System.Text.Json;
using NetScope.CrossRefs;
using Xunit;

namespace NetScope.Tests;

public class CrossRefTests
{
    private static List<string> SelectedMethods(params string[] options)
    {
        var r = TestEnv.Run(new[] { TestEnv.SamplesPath, "--format", "json" }.Concat(options).ToArray());
        Assert.Equal(0, r.ExitCode);
        using var doc = JsonDocument.Parse(r.Stdout);
        return doc.RootElement.GetProperty("assemblies")[0].GetProperty("methods").EnumerateArray()
            .Select(m => m.GetProperty("type").GetString() + "::" + m.GetProperty("name").GetString()).ToList();
    }

    [Fact]
    public void UsesFindsCallersOfMethodsAndTypes()
    {
        Assert.Equal(new[] { "MyApp.Auth.LoginService::Login" }, SelectedMethods("--uses", "MyApp.Db.Users::Find"));
        // A type mask matches every member, constructors included.
        Assert.Equal(new[] { "MyApp.Auth.LoginService::Login", "MyApp.Auth.LoginService::.ctor" }, SelectedMethods("--uses", "MyApp.Db.Users"));
        // Generic arity and nested-type separators in the mask are tolerated.
        Assert.Contains("MyApp.Flow.GenericUse::Length", SelectedMethods("--uses", "MyApp.Flow.Generic`1::Create"));
    }

    [Fact]
    public void FindStringSupportsSubstringMaskAndRegex()
    {
        Assert.Equal(new[] { "MyApp.Flow.Branches::Switch" }, SelectedMethods("--find-string", "thr"));
        Assert.Equal(new[] { "MyApp.Flow.Branches::Switch" }, SelectedMethods("--find-string", "t?o"));
        Assert.Equal(new[] { "MyApp.Auth.AdminLoginService::Login" }, SelectedMethods("--find-string", "^adm", "--regex"));
        Assert.Empty(SelectedMethods("--find-string", "no such literal"));
    }

    [Fact]
    public void DifferentFiltersAreCombined()
    {
        // Switch has "one" but calls nothing from Users: both conditions must hold.
        Assert.Empty(SelectedMethods("--find-string", "one", "--uses", "MyApp.Db.Users"));
        Assert.Equal(new[] { "MyApp.Auth.LoginService::Login" }, SelectedMethods("--uses", "MyApp.Db.Users", "--filter", "*::Login"));
    }

    [Fact]
    public void FieldRefsReportAccessKinds()
    {
        var r = TestEnv.Run(TestEnv.SamplesPath, "--format", "json", "--field-refs", "MyApp.Db.Users::_byName");
        using var doc = JsonDocument.Parse(r.Stdout);
        var methods = doc.RootElement.GetProperty("assemblies")[0].GetProperty("methods").EnumerateArray().ToList();
        Assert.Equal(new[] { ".ctor", "Add", "Find" }, methods.Select(m => m.GetProperty("name").GetString()).Order(StringComparer.Ordinal));
        var ctor = methods.First(m => m.GetProperty("name").GetString() == ".ctor");
        var match = Assert.Single(ctor.GetProperty("matches").EnumerateArray());
        Assert.Equal("field", match.GetProperty("query").GetString());
        Assert.Equal("write", match.GetProperty("kind").GetString());
    }

    [Fact]
    public void InvalidRegexIsABadArgument()
    {
        var r = TestEnv.Run(TestEnv.SamplesPath, "--find-string", "(", "--regex");
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("invalid regular expression", r.Stderr);
        Assert.Equal(2, TestEnv.Run(TestEnv.SamplesPath, "--regex").ExitCode);
    }

    [Fact]
    public void SearchCoversAllFilesOfTheGroup()
    {
        var r = TestEnv.Run(TestEnv.SamplesPath, TestEnv.PdbSamplePath, "--find-string", "large", "--no-color");
        Assert.Contains("Method   : string Describe(int total)", r.Stdout);
        Assert.Contains("ldstr     \"large\"  // Calculator.cs:", r.Stdout);
    }

    [Fact]
    public void LongLiteralsAreClipped()
    {
        var clipped = CrossReferenceQuery.Clip(new string('x', 500));
        Assert.Equal(CrossReferenceQuery.MaxLiteralLength + 3, clipped.Length);
        Assert.EndsWith("...", clipped);
    }
}
