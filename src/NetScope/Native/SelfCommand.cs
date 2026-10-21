namespace NetScope.Native;

/// <summary>
/// How to start NetScope itself in a helper mode: the single-file executable, or "dotnet netscope.dll"
/// for framework-dependent runs (including test hosts, where the current process is not NetScope).
/// </summary>
public sealed record SelfCommand(string FileName, IReadOnlyList<string> PrefixArguments)
{
    public static SelfCommand Locate()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("cannot locate the netscope executable");
        var entry = Path.GetFileNameWithoutExtension(exe);
        if (entry.Equals("netscope", StringComparison.OrdinalIgnoreCase)) return new SelfCommand(exe, Array.Empty<string>());

        // Framework-dependent: the current process is the dotnet host or a test host.
        // netscope.dll sits in the application directory (also when copied next to a test host).
        var dll = Path.Combine(AppContext.BaseDirectory, (typeof(SelfCommand).Assembly.GetName().Name ?? "netscope") + ".dll");
        if (!File.Exists(dll)) throw new InvalidOperationException("cannot locate " + dll);
        return new SelfCommand(FindDotnetHost(exe), new[] { dll });
    }

    /// <summary>The dotnet host (muxer) used to launch a framework-dependent `app.dll`.</summary>
    public static string DotnetHost() => FindDotnetHost(Environment.ProcessPath ?? (OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));

    private static string FindDotnetHost(string currentExe)
    {
        if (Path.GetFileNameWithoutExtension(currentExe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return currentExe;
        var hostName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        // Set by the dotnet CLI for child processes such as test hosts.
        var fromCli = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(fromCli) && File.Exists(fromCli)) return fromCli;
        // <root>/shared/Microsoft.NETCore.App/<version>/ -> <root>/dotnet
        var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var root = Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", ".."));
        if (File.Exists(Path.Combine(root, hostName))) return Path.Combine(root, hostName);
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(dotnetRoot) && File.Exists(Path.Combine(dotnetRoot, hostName))) return Path.Combine(dotnetRoot, hostName);
        return hostName; // resolved through PATH
    }
}
