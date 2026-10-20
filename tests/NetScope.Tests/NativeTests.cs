using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;

namespace NetScope.Tests;

public class NativeTests
{
    private static JsonElement Method(JsonDocument doc, string name) =>
        doc.RootElement.GetProperty("assemblies")[0].GetProperty("methods").EnumerateArray().First(m => m.GetProperty("name").GetString() == name);

    private static JsonDocument NativeJson(string path, params string[] filters)
    {
        var args = new List<string> { path, "--native", "--format", "json" };
        foreach (var f in filters) args.AddRange(new[] { "--filter", f });
        var r = TestEnv.Run(args.ToArray());
        Assert.Equal(0, r.ExitCode);
        return JsonDocument.Parse(r.Stdout);
    }

    /// <summary>Every exception handler of the method is compiled into its own funclet after the main body.</summary>
    [Fact]
    public void ReadyToRunFuncletsFollowTheMainBody()
    {
        using var doc = NativeJson(TestEnv.SamplesR2RPath, "*::NestedTry");
        var info = Method(doc, "NestedTry").GetProperty("nativeInfo");
        Assert.True(info.GetProperty("available").GetBoolean());
        var parts = info.GetProperty("parts").EnumerateArray().ToList();
        // catch FormatException, finally, filter, catch-when
        Assert.Equal(new[] { "main", "funclet 1", "funclet 2", "funclet 3", "funclet 4" }, parts.Select(p => p.GetProperty("name").GetString()));
        for (int i = 1; i < parts.Count; i++)
        {
            ulong prevEnd = parts[i - 1].GetProperty("address").GetUInt64() + (ulong)parts[i - 1].GetProperty("size").GetInt32();
            Assert.True(parts[i].GetProperty("address").GetUInt64() >= prevEnd, "funclets must not overlap");
        }
        var lines = Method(doc, "NestedTry").GetProperty("native").EnumerateArray().ToList();
        Assert.Contains(lines, l => l.GetProperty("part").GetString() == "funclet 4");
    }

    [Fact]
    public void ReadyToRunGenericInstantiations()
    {
        using var doc = NativeJson(TestEnv.SamplesR2RPath, "MyApp.Flow.Generic`1::*");
        var map = Method(doc, "Map").GetProperty("nativeInfo");
        Assert.True(map.GetProperty("available").GetBoolean());
        var names = map.GetProperty("parts").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToList();
        Assert.Contains("Generic<__Canon>::Map<__Canon>", names);
        var create = Method(doc, "Create").GetProperty("nativeInfo");
        Assert.Contains("Generic<__Canon>::Create", create.GetProperty("parts").EnumerateArray().Select(p => p.GetProperty("name").GetString()));
    }

    [Fact]
    public void FrameworkGenericInstantiationsIncludeValueTypes()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
        using var doc = NativeJson(typeof(object).Assembly.Location, "System.Collections.Generic.List`1::Add");
        var names = Method(doc, "Add").GetProperty("nativeInfo").GetProperty("parts").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToList();
        Assert.Contains("List<int>::Add", names);
        Assert.Contains(names, n => n!.Contains("__Canon"));
    }

    [Fact]
    public void NonGenericR2RMethodHasOnlyMainBody()
    {
        using var doc = NativeJson(TestEnv.SamplesR2RPath, "*::Loop");
        var parts = Method(doc, "Loop").GetProperty("nativeInfo").GetProperty("parts").EnumerateArray().ToList();
        Assert.Single(parts);
        Assert.Equal("main", parts[0].GetProperty("name").GetString());
    }

    /// <summary>C++/CLI image from .NET Framework (Windows only): managed part analyzed, native methods disassembled from the image.</summary>
    [Fact]
    public void MixedModeAssembly()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "Framework64", "v4.0.30319", "System.EnterpriseServices.Wrapper.dll");
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return;

        var r = TestEnv.Run(path, "--all", "--format", "json");
        Assert.Equal(0, r.ExitCode);
        using var doc = JsonDocument.Parse(r.Stdout);
        var asm = doc.RootElement.GetProperty("assemblies")[0];
        Assert.True(asm.GetProperty("mixedMode").GetBoolean());
        var methods = asm.GetProperty("methods").EnumerateArray().ToList();
        Assert.All(methods, m => Assert.Equal(0, m.GetProperty("errors").GetArrayLength()));

        var native = methods.Where(m => m.GetProperty("kinds").EnumerateArray().Any(k => k.GetString() == "native")).ToList();
        Assert.NotEmpty(native);
        Assert.All(native, m =>
        {
            var info = m.GetProperty("nativeInfo");
            Assert.True(info.GetProperty("available").GetBoolean(), m.GetProperty("name").GetString());
            Assert.Contains("outside the managed part", info.GetProperty("tier").GetString());
            Assert.False(m.TryGetProperty("il", out _));
        });

        // IL methods of the managed part are analyzed as usual.
        Assert.Contains(methods, m => m.TryGetProperty("il", out var il) && il.GetArrayLength() > 0 && m.GetProperty("pseudocodeSource").GetString() == "decompiler");

        // IJW imports have no DLL in metadata; their stub is a jump through the import table.
        var close = methods.First(m => m.GetProperty("name").GetString() == "CloseHandle");
        Assert.Equal("", close.GetProperty("pinvoke").GetProperty("dll").GetString());
        Assert.Contains(close.GetProperty("native").EnumerateArray(), l => l.GetProperty("text").GetString()!.StartsWith("jmp"));
    }
}
