using System.Text.Json;
using NetScope.Sandboxing;
using Xunit;

namespace NetScope.Tests;

public class SandboxTests
{
    private static string NativeReason(params string[] extra)
    {
        var args = new[] { TestEnv.HostilePath, "--native", "--allow-run", "--format", "json", "--filter", "Hostile.Target::Add" }.Concat(extra).ToArray();
        var r = TestEnv.Run(args);
        Assert.Equal(0, r.ExitCode);
        using var doc = JsonDocument.Parse(r.Stdout);
        var info = doc.RootElement.GetProperty("assemblies")[0].GetProperty("methods")[0].GetProperty("nativeInfo");
        Assert.False(info.GetProperty("available").GetBoolean());
        return info.GetProperty("reason").GetString()!;
    }

    /// <summary>2.3: the module initializer cannot start a shell or take 4 GB; the reason says so.</summary>
    [Fact]
    public void JitHelperBlocksChildProcessesAndLargeAllocations()
    {
        var reason = NativeReason();
        Assert.True(reason.Contains("hostile:", StringComparison.Ordinal), reason);
        Assert.True(reason.Contains("child process blocked", StringComparison.Ordinal), reason);
        Assert.False(reason.Contains("STARTED", StringComparison.Ordinal), reason);
        Assert.True(reason.Contains("allocation of 4 GB failed", StringComparison.Ordinal), reason);
    }

    [Fact]
    public void RunMemoryOptionIsValidated()
    {
        Assert.Equal(2, TestEnv.Run(TestEnv.SamplesPath, "--run-memory", "lots").ExitCode);
        Assert.True(Program.TryParseSize("512M", out var m) && m == 512L << 20);
        Assert.True(Program.TryParseSize("2g", out var g) && g == 2L << 30);
        Assert.True(Program.TryParseSize("100", out var plain) && plain == 100L << 20);
        Assert.False(Program.TryParseSize("-1G", out _));
    }

    [Fact]
    public void OptionsRoundTripToTheChild()
    {
        var o = new SandboxOptions { MemoryLimitBytes = 123456789, AllowChildProcesses = true, KillOnParentExit = false, Relaxed = new HashSet<string> { SandboxFeature.Memory } };
        var back = Sandbox.Decode(Sandbox.Encode(o));
        Assert.Equal(o.MemoryLimitBytes, back.MemoryLimitBytes);
        Assert.True(back.AllowChildProcesses);
        Assert.False(back.KillOnParentExit);
        Assert.Equal(new[] { SandboxFeature.Memory }, back.Relaxed);
    }

    [Fact]
    public void ChildEnvironmentIsAnAllowList()
    {
        Environment.SetEnvironmentVariable("NETSCOPE_TEST_SECRET", "hunter2");
        try
        {
            var dir = Sandbox.CreateWorkingDirectory();
            try
            {
                var psi = Sandbox.CreateStartInfo("x", Array.Empty<string>(), new SandboxOptions(), dir);
                Assert.False(psi.Environment.ContainsKey("NETSCOPE_TEST_SECRET"));
                Assert.Equal(dir, psi.Environment["TEMP"]);
                Assert.Equal(dir, psi.WorkingDirectory);
                Assert.True(psi.Environment.ContainsKey("DOTNET_GCHeapHardLimit"));
                Assert.All(psi.Environment.Keys, k => Assert.True(
                    k.Equals("PATH", StringComparison.OrdinalIgnoreCase) || k.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) ||
                    k is "TEMP" or "TMP" or "TMPDIR" or "NETSCOPE_SANDBOX" || k.Equals("SystemRoot", StringComparison.OrdinalIgnoreCase), k));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("NETSCOPE_TEST_SECRET", null);
        }
    }
}
