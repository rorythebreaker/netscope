using NetScope.Catalog;
using Xunit;

namespace NetScope.Tests;

public class CatalogTests
{
    private static MethodModel Find(IReadOnlyList<MethodModel> methods, string fullName, int index = 0) =>
        methods.Where(m => m.FullName == fullName).ElementAt(index);

    [Theory]
    [InlineData("MyApp.Auth.LoginService::Login", "instance", "virtual")]
    [InlineData("MyApp.Auth.AdminLoginService::Login", "instance", "override")]
    [InlineData("MyApp.Auth.LoginService::Hash", "static")]
    [InlineData("MyApp.Flow.Features::.ctor", "ctor", "instance")]
    [InlineData("MyApp.Flow.Features::.cctor", "cctor", "static")]
    [InlineData("MyApp.Flow.Features::Finalize", "finalizer", "override")]
    [InlineData("MyApp.Flow.Features::get_Instances", "property-get", "static")]
    [InlineData("MyApp.Flow.Features::set_Instances", "property-set")]
    [InlineData("MyApp.Flow.Features::add_Changed", "event-add")]
    [InlineData("MyApp.Flow.Features::remove_Changed", "event-remove")]
    [InlineData("MyApp.Flow.Features::op_Addition", "operator", "static")]
    [InlineData("MyApp.Flow.Features::op_Implicit", "conversion")]
    [InlineData("MyApp.Flow.Features::ComputeAsync", "async")]
    [InlineData("MyApp.Flow.Features::Range", "iterator")]
    [InlineData("MyApp.Flow.Features::Ticks", "pinvoke", "static", "no-body")]
    [InlineData("MyApp.Flow.Generic`1::Map", "generic", "instance")]
    [InlineData("MyApp.Flow.Shape::Area", "abstract", "no-body")]
    [InlineData("MyApp.Flow.Circle::Area", "override")]
    [InlineData("MyApp.Auth.ILoginService::Login", "abstract", "no-body")]
    public void Kinds(string fullName, params string[] expected)
    {
        var (asm, _, methods) = TestEnv.LoadSamples();
        using (asm)
        {
            var m = Find(methods, fullName);
            foreach (var k in expected) Assert.Contains(k, m.Kinds);
        }
    }

    [Fact]
    public void LambdasAndLocalFunctionsAreRecognized()
    {
        var (asm, _, methods) = TestEnv.LoadSamples();
        using (asm)
        {
            Assert.Contains(methods, m => m.Name.Contains(">b__") && m.Kinds.Contains("lambda") && m.Kinds.Contains("compiler-generated"));
            Assert.Contains(methods, m => m.Name.Contains(">g__Local") && m.Kinds.Contains("local-function"));
            Assert.Contains(methods, m => m.Name == "MoveNext" && m.Kinds.Contains("state-machine"));
        }
    }

    [Fact]
    public void PInvokeTargetIsReported()
    {
        var (asm, _, methods) = TestEnv.LoadSamples();
        using (asm)
        {
            var m = Find(methods, "MyApp.Flow.Features::Ticks");
            Assert.Equal("kernel32.dll", m.PInvoke!.Dll);
            Assert.Equal("GetTickCount", m.PInvoke.EntryPoint);
        }
    }

    [Fact]
    public void SignatureAndAccess()
    {
        var (asm, _, methods) = TestEnv.LoadSamples();
        using (asm)
        {
            var m = Find(methods, "MyApp.Auth.LoginService::Login");
            Assert.Equal("bool Login(string user, string pass)", m.Signature);
            Assert.Equal("public", m.Access);
            Assert.Equal("MyApp.Auth", m.Namespace);
            Assert.Equal("instance, public, virtual", string.Join(", ", m.DisplayKinds()));
            Assert.Equal("TOut Map<TOut>(Func<T, TOut> map)", Find(methods, "MyApp.Flow.Generic`1::Map").Signature);
        }
    }

    [Fact]
    public void MethodsFollowMetadataOrder()
    {
        var (asm, _, methods) = TestEnv.LoadSamples();
        using (asm)
        {
            var tokens = methods.Select(m => m.Token).ToList();
            Assert.Equal(tokens.OrderBy(t => t), tokens);
            Assert.Equal(asm.Metadata.MethodDefinitions.Count, methods.Count);
        }
    }

    [Theory]
    [InlineData("MyApp.Auth.*::Login*", 3)]
    [InlineData("*::Login", 3)]
    [InlineData("myapp.flow.branches::*", 4)]
    public void FilterMasks(string mask, int expected)
    {
        var (asm, _, methods) = TestEnv.LoadSamples();
        using (asm)
        {
            var filter = new MethodFilter(new[] { mask }, Array.Empty<string>());
            Assert.Equal(expected, methods.Count(filter.Matches));
        }
    }

    [Fact]
    public void FilterByType()
    {
        var (asm, _, methods) = TestEnv.LoadSamples();
        using (asm)
        {
            var filter = new MethodFilter(Array.Empty<string>(), new[] { "async,pinvoke" });
            var selected = methods.Where(filter.Matches).Select(m => m.Name).ToList();
            Assert.Equal(new[] { "ComputeAsync", "Ticks" }, selected);
            Assert.Throws<ArgumentException>(() => new MethodFilter(Array.Empty<string>(), new[] { "nonsense" }));
        }
    }
}
