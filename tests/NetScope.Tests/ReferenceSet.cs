using Xunit;

namespace NetScope.Tests;

/// <summary>One assembly of the reference set (TZ-NetScope-3 §3.2, tests/Reference/README.md).</summary>
public sealed record ReferenceAssembly(int Number, string Id, string Path, IReadOnlyList<string> RefPaths, bool Obfuscated, bool ReadyToRun)
{
    /// <summary>Command line for NetScope: the file, no colors, its reference directories, then the options.</summary>
    public string[] Args(params string[] options) =>
        new[] { Path, "--no-color" }.Concat(RefPaths.SelectMany(r => new[] { "--ref-path", r })).Concat(options).ToArray();

    public override string ToString() => Id;
}

internal static class ReferenceSet
{
    private static readonly (int Number, string Id, string Project)[] Built =
    {
        (1, "01-ConsoleApp", "Ref01.ConsoleApp"),
        (2, "02-Library", "Ref02.Library"),
        (3, "03-NetStandard", "Ref03.NetStandard"),
        (4, "04-NetFx48", "Ref04.NetFx48"),
        (5, "05-NetFx35", "Ref05.NetFx35"),
        (6, "06-NetCore31", "Ref06.NetCore31"),
        (7, "07-Async", "Ref07.Async"),
        (8, "08-Iterators", "Ref08.Iterators"),
        (9, "09-Generics", "Ref09.Generics"),
        (10, "10-Interop", "Ref10.Interop"),
        (11, "11-Unsafe", "Ref11.Unsafe"),
        (12, "12-HandIL", "Ref12.HandIL"),
        (13, "13-Switches", "Ref13.Switches"),
        (14, "14-Lambdas", "Ref14.Lambdas"),
        (15, "15-Modern", "Ref15.Modern"),
        (16, "16-VbLibrary", "Ref16.VbLibrary"),
        (17, "17-FSharpLibrary", "Ref17.FSharpLibrary"),
    };

    private static readonly Lazy<IReadOnlyList<ReferenceAssembly>> Assemblies = new(Load);

    public static IReadOnlyList<ReferenceAssembly> All => Assemblies.Value;

    public static ReferenceAssembly Get(string id) => All.Single(a => a.Id == id);

    /// <summary>The id of every assembly of the set, in table order.</summary>
    public static string[] IdList { get; } =
        Built.Select(b => b.Id).Concat(new[] { "18-Library-R2R-win-x64", "19-Library-R2R-linux-arm64", "20-Obfuscated" }).ToArray();

    /// <summary>Theory data: the id of every assembly of the set.</summary>
    public static TheoryData<string> Ids
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var id in IdList) data.Add(id);
            return data;
        }
    }

    private static string Configuration =>
        AppContext.BaseDirectory.Contains($"{System.IO.Path.DirectorySeparatorChar}Release{System.IO.Path.DirectorySeparatorChar}") ? "Release" : "Debug";

    private static List<ReferenceAssembly> Load()
    {
        var root = System.IO.Path.Combine(TestEnv.RepoRoot, "tests", "Reference", "bin");
        var list = new List<ReferenceAssembly>();
        foreach (var (number, id, project) in Built)
        {
            var path = System.IO.Path.Combine(root, Configuration, project, project + ".dll");
            list.Add(new ReferenceAssembly(number, id, Existing(path), RefsOf(path), Obfuscated: false, ReadyToRun: false));
        }

        var libraryRefs = RefsOf(System.IO.Path.Combine(root, Configuration, "Ref02.Library", "Ref02.Library.dll"));
        foreach (var (number, rid) in new[] { (18, "win-x64"), (19, "linux-arm64") })
        {
            var path = System.IO.Path.Combine(root, "r2r", Configuration, rid, "Ref02.Library.dll");
            list.Add(new ReferenceAssembly(number, $"{number}-Library-R2R-{rid}", Existing(path), libraryRefs, Obfuscated: false, ReadyToRun: true));
        }

        // The ConfuserEx fixture targets .NET Framework: resolve mscorlib from the same reference assemblies as #4,
        // so the result does not depend on whether the machine has .NET Framework installed.
        var fxRefs = RefsOf(System.IO.Path.Combine(root, Configuration, "Ref04.NetFx48", "Ref04.NetFx48.dll"));
        list.Add(new ReferenceAssembly(20, "20-Obfuscated", Existing(TestEnv.ObfuscatedFixture("confuserex-normal.exe")), fxRefs, Obfuscated: true, ReadyToRun: false));
        return list;
    }

    private static string Existing(string path) =>
        File.Exists(path) ? path : throw new FileNotFoundException("the reference set is built with tests/NetScope.Tests (dotnet build)", path);

    private static string[] RefsOf(string assemblyPath)
    {
        var refs = System.IO.Path.ChangeExtension(assemblyPath, ".refs");
        return File.Exists(refs)
            ? File.ReadAllLines(refs).Select(l => l.Trim().TrimEnd('/', '\\')).Where(l => l.Length > 0).ToArray()
            : Array.Empty<string>();
    }
}
