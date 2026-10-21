using System.Diagnostics;
using System.Runtime.InteropServices;
using NetScope.Native;
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
        var r = TestEnv.Run(Debug, TestEnv.SamplesPath, "--allow-run", "--i-know-running-as-admin", "--format", "xml", "-q");
        Assert.Equal(Analyzer.ExitBadArguments, r.ExitCode);
    }

    [Fact]
    public void NonManagedFileWithAllowRunIsRejected()
    {
        var dir = TestEnv.TempDir();
        var text = Path.Combine(dir, "notmanaged.dll");
        File.WriteAllText(text, new string('x', 256));
        var r = TestEnv.Run(Debug, text, "--allow-run", "--i-know-running-as-admin", "-q");
        Assert.Equal(Analyzer.ExitNothingOpened, r.ExitCode);
        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public void MissingFileWithAllowRunIsRejected()
    {
        var r = TestEnv.Run(Debug, Path.Combine(TestEnv.TempDir(), "nope.dll"), "--allow-run", "--i-know-running-as-admin", "-q");
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
        var r = TestEnv.Run(Debug, TestEnv.DebugTargetPath, "--allow-run", "--i-know-running-as-admin", "--script", script);

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
        var r = TestEnv.Run(Debug, TestEnv.DebugTargetPath, "--allow-run", "--i-know-running-as-admin", "--script", script, "--format", "json");

        Assert.Equal(Analyzer.ExitOk, r.ExitCode);
        Assert.Contains("\"event\":\"stopped\"", r.Stdout);
        Assert.Contains("\"method\":\"NetScope.Samples.DebugTarget.Program::Main\"", r.Stdout);
    }

    [Fact]
    public async Task ConcurrentSessionsAllStopOnEntry()
    {
        // Regression: the attach used to run after the runtime-startup callback had returned, so on a loaded
        // machine the target could run Main (or exit) before the entry breakpoint existed. Separate processes:
        // TestEnv.Run redirects the global Console and cannot run in parallel.
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;

        var script = Path.Combine(TestEnv.TempDir(), "steps.txt");
        File.WriteAllText(script, "continue\n");
        var netscope = Path.Combine(AppContext.BaseDirectory, "netscope.dll");
        var processes = Enumerable.Range(0, 8).Select(_ =>
        {
            var psi = new ProcessStartInfo(SelfCommand.DotnetHost()) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { netscope, Debug, TestEnv.DebugTargetPath, "--allow-run", "--i-know-running-as-admin", "--script", script, "--format", "json" })
                psi.ArgumentList.Add(a);
            return Process.Start(psi)!;
        }).ToArray();

        foreach (var p in processes)
        {
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderr = await p.StandardError.ReadToEndAsync();
            var stdout = await stdoutTask;
            Assert.True(p.WaitForExit(120_000), "a debug session did not finish");
            Assert.True(p.ExitCode == Analyzer.ExitOk, $"exit {p.ExitCode}: {stderr}");
            Assert.True(stdout.Contains("\"event\":\"stopped\""), "no stop on entry: " + stderr);
            p.Dispose();
        }
    }
}
