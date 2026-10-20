using NetScope.Catalog;
using NetScope.Diagnostics;
using NetScope.Loading;
using NetScope.Metadata;

[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace NetScope.Tests;

/// <summary>Locations of test inputs and helpers to run the tool in-process.</summary>
internal static class TestEnv
{
    public static string RepoRoot { get; } = FindRepoRoot();

    /// <summary>The sample assembly built next to the tests (same configuration).</summary>
    public static string SamplesPath
    {
        get
        {
            var config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
            var path = Path.Combine(RepoRoot, "tests", "NetScope.Samples", "bin", config, "net8.0", "NetScope.Samples.dll");
            if (!File.Exists(path)) throw new FileNotFoundException("build tests/NetScope.Samples first", path);
            return path;
        }
    }

    /// <summary>The sample assembly compiled with ReadyToRun for the build machine (see NetScope.Tests.csproj).</summary>
    public static string SamplesR2RPath
    {
        get
        {
            var config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
            var path = Path.Combine(RepoRoot, "tests", "NetScope.Samples", "bin", "r2r", config, "NetScope.Samples.dll");
            if (!File.Exists(path)) throw new FileNotFoundException("ReadyToRun sample is built with tests/NetScope.Tests", path);
            return path;
        }
    }

    public static string GoldenDir => Path.Combine(RepoRoot, "tests", "NetScope.Tests", "Golden");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "NetScope.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("NetScope.sln not found above " + AppContext.BaseDirectory);
    }

    public sealed record CliResult(int ExitCode, string Stdout, string Stderr);

    public static CliResult Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var oldErr = Console.Error;
        Console.SetError(stderr);
        Log.SetWriter(stderr);
        try
        {
            int code = Program.Run(args, stdout);
            return new CliResult(code, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetError(oldErr);
            Log.SetWriter(oldErr);
            Log.Level = Verbosity.Normal;
        }
    }

    public static (LoadedAssembly Asm, MemberResolver Resolver, IReadOnlyList<MethodModel> Methods) LoadSamples()
    {
        var asm = new AssemblyLoader().TryLoad(0, SamplesPath, out var reason) ?? throw new InvalidOperationException(reason);
        var resolver = new MemberResolver(asm.Metadata, asm.Name);
        var methods = new MethodCatalog().Build(asm, resolver, MethodFilter.All);
        return (asm, resolver, methods);
    }

    public static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "netscope-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
