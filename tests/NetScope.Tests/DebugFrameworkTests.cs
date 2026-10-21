using System.Runtime.InteropServices;
using NetScope.Pipeline;
using Xunit;

namespace NetScope.Tests;

/// <summary>
/// The .NET Framework backend of the debugger (TZ-NetScope-2 §4): the same program as <see cref="DebugTests"/>,
/// built for .NET Framework 4.8, debugged through mscordbi. The session itself (stops, stack, breakpoints, steps,
/// code views, JSON) is shared with the .NET Core backend, so the same checks apply. Windows x64 only; elsewhere the
/// command must fail with a clear message.
/// </summary>
public sealed class DebugFrameworkTests
{
    private const string Debug = "debug";

    private static bool Supported => OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private static TestEnv.CliResult RunScript(string commands, params string[] extra)
    {
        var script = Path.Combine(TestEnv.TempDir(), "steps.txt");
        File.WriteAllText(script, commands);
        return TestEnv.Run(new[] { Debug, TestEnv.DebugTargetFrameworkPath, "--allow-run", "--i-know-running-as-admin", "--script", script }.Concat(extra).ToArray());
    }

    [Fact]
    public void StopsOnEntryAndShowsStack()
    {
        if (!Supported) return;
        var r = RunScript("stack\ncontinue\n");

        Assert.True(r.ExitCode == Analyzer.ExitOk, $"exit {r.ExitCode}: {r.Stderr}");
        Assert.Contains("Stopped (entry) at NetScope.Samples.DebugTarget.Program::Main", r.Stdout);
        Assert.Contains("debug-target result 42", r.Stderr);   // it ran to the end after continue
        Assert.Contains("SHA-256", r.Stderr);
    }

    [Fact]
    public void BreakpointStepAndCodeViewsWork()
    {
        if (!Supported) return;
        var r = RunScript("break NetScope.Samples.DebugTarget.Program::Add\ncontinue\nstack\nil\npseudo\nstep\ncontinue\n");

        Assert.True(r.ExitCode == Analyzer.ExitOk, $"exit {r.ExitCode}: {r.Stderr}");
        Assert.Contains("Breakpoint 1 at NetScope.Samples.DebugTarget.Program::Add", r.Stdout);
        Assert.Contains("Stopped (breakpoint) at NetScope.Samples.DebugTarget.Program::Add", r.Stdout);
        Assert.Contains("#1  NetScope.Samples.DebugTarget.Program::Main", r.Stdout);
        Assert.Contains("IL_0000", r.Stdout);
        Assert.Contains("int Add(int a, int b)", r.Stdout);
        Assert.Contains("Stopped (step)", r.Stdout);
    }

    [Fact]
    public void JsonFormatEmitsStoppedEvent()
    {
        if (!Supported) return;
        var r = RunScript("continue\n", "--format", "json");

        Assert.True(r.ExitCode == Analyzer.ExitOk, $"exit {r.ExitCode}: {r.Stderr}");
        Assert.Contains("\"event\":\"stopped\"", r.Stdout);
        Assert.Contains("\"method\":\"NetScope.Samples.DebugTarget.Program::Main\"", r.Stdout);
    }

    /// <summary>§4.4: on Linux a .NET Framework image gives a clear error, not a crash.</summary>
    [Fact]
    public void OutsideWindowsAFrameworkProgramIsRefusedWithAReason()
    {
        if (OperatingSystem.IsWindows()) return;
        var r = RunScript("continue\n");

        Assert.Equal(Analyzer.ExitNothingOpened, r.ExitCode);
        Assert.Contains("debugging a .NET Framework program needs Windows", r.Stderr);
    }
}
