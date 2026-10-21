using System.Runtime.InteropServices;
using NetScope.Pipeline;
using Xunit;

namespace NetScope.Tests;

/// <summary>
/// The read-only debugger (TZ-NetScope-2 §3). The safety gates (§3.4) run on every platform; the actual
/// launch-and-stack path (§3.6) uses ICorDebug and is verified on Windows x64 only.
/// </summary>
public sealed class DebugTests
{
    private const string Debug = "debug";

    // --- §3.4 / §3.6 safety gates (cross-platform, no process launched) ---

    [Fact]
    public void WithoutAllowRunNothingRunsAndExitsTwo()
    {
        // §3.6: without --allow-run the command must not create any process. A marker target would stay untouched.
        var marker = Path.Combine(TestEnv.TempDir(), "ran.marker");
        var r = TestEnv.Run(Debug, TestEnv.SamplesPath, "-q");
        Assert.Equal(Analyzer.ExitBadArguments, r.ExitCode);
        Assert.False(File.Exists(marker));
        Assert.DoesNotContain("Stopped", r.Stdout); // never reached the launch
    }

    [Fact]
    public void BadFormatExitsTwo()
    {
        var r = TestEnv.Run(Debug, TestEnv.SamplesPath, "--allow-run", "--format", "xml", "-q");
        Assert.Equal(Analyzer.ExitBadArguments, r.ExitCode);
    }

    [Fact]
    public void NonManagedFileWithAllowRunIsRejected()
    {
        var dir = TestEnv.TempDir();
        var text = Path.Combine(dir, "notmanaged.dll");
        File.WriteAllText(text, new string('x', 256));
        var r = TestEnv.Run(Debug, text, "--allow-run", "-q");
        Assert.Equal(Analyzer.ExitNothingOpened, r.ExitCode);
        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public void MissingFileWithAllowRunIsRejected()
    {
        var r = TestEnv.Run(Debug, Path.Combine(TestEnv.TempDir(), "nope.dll"), "--allow-run", "-q");
        Assert.Equal(Analyzer.ExitNothingOpened, r.ExitCode);
    }

    // --- §3.6 end-to-end: stop on Main and show the stack (Windows x64) ---

    [Fact]
    public void StopsOnEntryAndShowsStack()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return; // ICorDebug backend is Windows x64 in this stage; nothing to assert elsewhere.

        var script = Path.Combine(TestEnv.TempDir(), "steps.txt");
        File.WriteAllText(script, "stack\ncontinue\n");
        var r = TestEnv.Run(Debug, TestEnv.DebugTargetPath, "--allow-run", "--script", script);

        Assert.Equal(Analyzer.ExitOk, r.ExitCode);
        // Stopped at the managed entry point, with Main on the stack.
        Assert.Contains("Stopped", r.Stdout);
        Assert.Contains("Program::Main", r.Stdout);
        // The target actually ran to completion after we continued.
        Assert.Contains("debug-target result 42", r.Stderr);
        // The SHA-256 of the file was shown (§3.4.2).
        Assert.Contains("SHA-256", r.Stderr);
    }

    [Fact]
    public void JsonFormatEmitsStoppedEvent()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;

        var script = Path.Combine(TestEnv.TempDir(), "steps.txt");
        File.WriteAllText(script, "continue\n");
        var r = TestEnv.Run(Debug, TestEnv.DebugTargetPath, "--allow-run", "--script", script, "--format", "json");

        Assert.Equal(Analyzer.ExitOk, r.ExitCode);
        Assert.Contains("\"event\":\"stopped\"", r.Stdout);
        Assert.Contains("\"method\":\"NetScope.Samples.DebugTarget.Program::Main\"", r.Stdout);
    }

    // --- §3.2/§3.3 Stage 3: breakpoints, stepping, code views ---

    [Fact]
    public void BreakpointOnAMethodIsHitWithTheRightStack()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;

        var script = Path.Combine(TestEnv.TempDir(), "bp.txt");
        File.WriteAllText(script, "break NetScope.Samples.DebugTarget.Program::Add\nbreaks\ncontinue\nstack\ncontinue\n");
        var r = TestEnv.Run(Debug, TestEnv.DebugTargetPath, "--allow-run", "--script", script);

        Assert.Equal(Analyzer.ExitOk, r.ExitCode);
        Assert.Contains("Breakpoint 1 at NetScope.Samples.DebugTarget.Program::Add", r.Stdout);
        Assert.Contains("Stopped (breakpoint) at NetScope.Samples.DebugTarget.Program::Add", r.Stdout);
        // The stack at Add is Add -> Main.
        Assert.Contains("#0  NetScope.Samples.DebugTarget.Program::Add", r.Stdout);
        Assert.Contains("#1  NetScope.Samples.DebugTarget.Program::Main", r.Stdout);
    }

    [Fact]
    public void StepProducesAStepStop()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;

        var script = Path.Combine(TestEnv.TempDir(), "step.txt");
        File.WriteAllText(script, "break NetScope.Samples.DebugTarget.Program::Add\ncontinue\nstep\ncontinue\n");
        var r = TestEnv.Run(Debug, TestEnv.DebugTargetPath, "--allow-run", "--script", script);

        Assert.Equal(Analyzer.ExitOk, r.ExitCode);
        Assert.Contains("Stopped (step)", r.Stdout);
    }

    [Fact]
    public void CodeViewsRenderTheStoppedMethod()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;

        var script = Path.Combine(TestEnv.TempDir(), "code.txt");
        File.WriteAllText(script, "break NetScope.Samples.DebugTarget.Program::Add\ncontinue\nil\ncfg\npseudo\ncontinue\n");
        var r = TestEnv.Run(Debug, TestEnv.DebugTargetPath, "--allow-run", "--script", script);

        Assert.Equal(Analyzer.ExitOk, r.ExitCode);
        Assert.Contains("IL_0000", r.Stdout);                 // il
        Assert.Contains("complexity", r.Stdout);              // cfg
        Assert.Contains("int Add(int a, int b)", r.Stdout);   // pseudo (decompiled)
    }
}
