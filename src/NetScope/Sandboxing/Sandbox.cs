using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace NetScope.Sandboxing;

/// <summary>
/// Starts NetScope helper processes that run untrusted code, with the restrictions of <see cref="SandboxOptions"/>.
/// <para>
/// Windows: the parent creates a Job Object (memory limit, one active process, kill on close) and assigns the
/// child to it. Linux: the child restricts itself (RLIMIT_AS, PR_SET_PDEATHSIG, no_new_privs, seccomp)
/// in <see cref="EnterChild"/>. In both cases the child waits for a "go" line on stdin, written only after
/// the restrictions are in place, so none of the analyzed code runs before them.
/// </para>
/// </summary>
public static class Sandbox
{
    /// <summary>Tells a helper process it is sandboxed and which restrictions to apply to itself.</summary>
    internal const string ConfigVariable = "NETSCOPE_SANDBOX";
    private const string GoLine = "go";
    /// <summary>Exit code of a child that could not apply a restriction.</summary>
    public const int ExitRestrictionFailed = 86;

    /// <summary>Environment variables passed through to the child; everything else is dropped.</summary>
    private static bool IsAllowed(string name) =>
        name.Equals("PATH", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) ||
        // Required by Windows itself for system DLLs, crypto and sockets to initialize.
        (OperatingSystem.IsWindows() && name.Equals("SystemRoot", StringComparison.OrdinalIgnoreCase));

    public static ProcessStartInfo CreateStartInfo(string fileName, IEnumerable<string> arguments, SandboxOptions options, string workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        var inherited = psi.Environment.Keys.ToList();
        foreach (var name in inherited)
            if (!IsAllowed(name)) psi.Environment.Remove(name);
        psi.Environment["TEMP"] = workingDirectory;
        psi.Environment["TMP"] = workingDirectory;
        psi.Environment["TMPDIR"] = workingDirectory;
        foreach (var (k, v) in options.ExtraEnvironment) psi.Environment[k] = v;
        if (options.MemoryLimitBytes > 0 && !options.Relaxed.Contains(SandboxFeature.Memory))
        {
            // The GC turns an over-limit allocation into OutOfMemoryException instead of a failed mmap.
            psi.Environment["DOTNET_GCHeapHardLimit"] = "0x" + options.MemoryLimitBytes.ToString("X", CultureInfo.InvariantCulture);
        }
        psi.Environment[ConfigVariable] = Encode(options);
        return psi;
    }

    /// <summary>
    /// Starts the child and applies the parent-side restrictions. Must be called on the thread that waits for
    /// the child: on Linux the parent-death signal is tied to the thread that started it.
    /// </summary>
    public static SandboxedProcess Start(ProcessStartInfo psi, SandboxOptions options, string workingDirectory)
    {
        WindowsJob? job = null;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                job = WindowsJob.Create(options);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new SandboxException(SandboxFeature.Memory, "cannot create a Job Object: " + ex.Message);
            }
        }

        var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
            if (job != null)
            {
                try
                {
                    job.Assign(process.Handle);
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    throw new SandboxException(SandboxFeature.NoChildProcesses, "cannot assign the helper process to the Job Object: " + ex.Message);
                }
            }
            // Restrictions are in place: let the child continue.
            process.StandardInput.WriteLine(GoLine);
            process.StandardInput.Flush();
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            process.Dispose();
            job?.Dispose();
            throw;
        }
        return new SandboxedProcess(process, job, workingDirectory);
    }

    /// <summary>Creates the private working directory of a sandboxed child.</summary>
    public static string CreateWorkingDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "netscope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ------------------------------------------------------------------ child side

    /// <summary>
    /// Called first thing in a helper process. Applies self restrictions (Linux) and waits for the parent's
    /// go-ahead. Does nothing when the process was not started through <see cref="Start"/>.
    /// </summary>
    public static void EnterChild()
    {
        var config = Environment.GetEnvironmentVariable(ConfigVariable);
        if (config == null) return;
        Environment.SetEnvironmentVariable(ConfigVariable, null);
        var options = Decode(config);

        if (OperatingSystem.IsLinux())
        {
            Apply(options, SandboxFeature.KillOnParentExit, options.KillOnParentExit, LinuxLimits.DieWithParent);
            Apply(options, SandboxFeature.Memory, options.MemoryLimitBytes > 0, () => LinuxLimits.ApplyMemoryLimit(options.MemoryLimitBytes));
            Apply(options, SandboxFeature.NoChildProcesses, !options.AllowChildProcesses, LinuxLimits.ForbidChildProcesses);
            LinuxLimits.DropNewPrivileges();
        }

        // Windows: the parent assigns the Job Object before writing this line.
        var line = Console.In.ReadLine();
        if (line != GoLine)
        {
            Console.Error.WriteLine("netscope: sandbox: the parent did not confirm the restrictions; exiting");
            Environment.Exit(ExitRestrictionFailed);
        }
    }

    private static void Apply(SandboxOptions options, string feature, bool wanted, Action apply)
    {
        if (!wanted || options.Relaxed.Contains(feature)) return;
        try
        {
            apply();
        }
        catch (Exception ex) when (ex is SandboxException or DllNotFoundException or EntryPointNotFoundException)
        {
            Console.Error.WriteLine($"netscope: sandbox: cannot apply restriction '{feature}': {ex.Message} (use --relax-sandbox {feature} to run without it)");
            Environment.Exit(ExitRestrictionFailed);
        }
    }

    internal static string Encode(SandboxOptions o) => string.Join(';',
        "memory=" + o.MemoryLimitBytes.ToString(CultureInfo.InvariantCulture),
        "children=" + (o.AllowChildProcesses ? "1" : "0"),
        "kill=" + (o.KillOnParentExit ? "1" : "0"),
        "relaxed=" + string.Join(',', o.Relaxed.Order(StringComparer.Ordinal)));

    internal static SandboxOptions Decode(string text)
    {
        var map = text.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);
        return new SandboxOptions
        {
            MemoryLimitBytes = map.TryGetValue("memory", out var m) && long.TryParse(m, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mem) ? mem : 0,
            AllowChildProcesses = map.GetValueOrDefault("children") == "1",
            KillOnParentExit = map.GetValueOrDefault("kill") != "0",
            Relaxed = (map.GetValueOrDefault("relaxed") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal),
        };
    }
}

/// <summary>A running sandboxed child. Disposing kills it, closes the job and deletes its working directory.</summary>
public sealed class SandboxedProcess : IDisposable
{
    private readonly WindowsJob? _job;
    private bool _disposed;

    internal SandboxedProcess(Process process, WindowsJob? job, string workingDirectory)
    {
        Process = process;
        _job = job;
        WorkingDirectory = workingDirectory;
    }

    public Process Process { get; }
    public string WorkingDirectory { get; }

    public void Kill()
    {
        _job?.Terminate();
        try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Kill();
        try { Process.WaitForExit(5000); } catch (InvalidOperationException) { }
        Process.Dispose();
        _job?.Dispose();
        for (int attempt = 0; attempt < 5 && Directory.Exists(WorkingDirectory); attempt++)
        {
            try { Directory.Delete(WorkingDirectory, recursive: true); }
            catch (IOException) { Thread.Sleep(100); }
            catch (UnauthorizedAccessException) { Thread.Sleep(100); }
        }
    }
}
