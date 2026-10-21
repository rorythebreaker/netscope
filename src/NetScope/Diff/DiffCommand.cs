using System.CommandLine;
using NetScope.Audit;
using NetScope.Catalog;
using NetScope.Diagnostics;
using NetScope.Loading;
using NetScope.Pipeline;

namespace NetScope.Diff;

/// <summary>
/// `netscope diff &lt;old&gt; &lt;new&gt;`: compares two versions of an assembly, or two directories whose files are
/// matched by assembly name. Exit codes: as the analyzer, and with --exit-code 0 (no differences) or 5.
/// </summary>
public static class DiffCommand
{
    public const string Name = "diff";
    public const int ExitDifferences = 5;

    public static int Run(string[] args, TextWriter stdout)
    {
        var root = new RootCommand("Compare two versions of an assembly (or two directories, files matched by assembly name). Methods are matched by name and signature; \"changed\" means their IL differs once tokens are replaced by full names.");
        var oldArg = new Argument<string>("old") { Description = "Old version: file or directory" };
        var newArg = new Argument<string>("new") { Description = "New version: file or directory" };
        var pseudo = new Option<bool>("--pseudo") { Description = "Also show the difference of the pseudocode of changed methods" };
        var audit = new Option<bool>("--audit") { Description = "Show only the security findings that are new in the second version" };
        var rules = new Option<string[]>("--rules") { Description = "With --audit: rule IDs to run" };
        var minLevel = new Option<string>("--min-level") { Description = "With --audit: drop findings below this level", DefaultValueFactory = _ => "note" };
        var format = new Option<string>("--format") { Description = "Output format", DefaultValueFactory = _ => "text" };
        format.AcceptOnlyFromAmong("text", "json");
        var output = new Option<string>("--out") { Description = "Write to a file" };
        var exitCode = new Option<bool>("--exit-code") { Description = "Exit with 0 if there are no differences and 5 if there are (like git diff)" };
        var filter = new Option<string[]>("--filter") { Description = "Compare only methods matching a full name mask (repeatable)" };
        var type = new Option<string[]>("--type") { Description = "Compare only methods of these kinds" };
        var refPath = new Option<string[]>("--ref-path") { Description = "Directory with dependencies (repeatable), used by --pseudo" };
        var recursive = new Option<bool>("--recursive", "-r") { Description = "Directories: include subdirectories" };
        var noColor = new Option<bool>("--no-color") { Description = "Disable colored output" };
        var quiet = new Option<bool>("--quiet", "-q") { Description = "Only errors in stderr" };
        root.Arguments.Add(oldArg);
        root.Arguments.Add(newArg);
        foreach (var o in new Option[] { pseudo, audit, rules, minLevel, format, output, exitCode, filter, type, refPath, recursive, noColor, quiet }) root.Options.Add(o);

        var parse = root.Parse(new[] { root.Name }.Concat(args).ToArray());
        if (parse.Errors.Count > 0)
        {
            foreach (var e in parse.Errors) Console.Error.WriteLine("netscope diff: error: " + e.Message);
            Console.Error.WriteLine("Try 'netscope diff --help' for more information.");
            return Analyzer.ExitBadArguments;
        }

        int code = Analyzer.ExitOk;
        root.SetAction(p =>
        {
            if (p.GetValue(quiet)) Log.Level = Verbosity.Quiet;
            MethodFilter methodFilter;
            Auditor? auditor = null;
            try
            {
                methodFilter = new MethodFilter(p.GetValue(filter) ?? Array.Empty<string>(), p.GetValue(type) ?? Array.Empty<string>());
                if (p.GetValue(audit))
                {
                    if (!RuleSet.TryParseLevel(p.GetValue(minLevel) ?? "note", out var level)) throw new ArgumentException("--min-level expects note, warning or error");
                    auditor = new Auditor(RuleSet.Select(p.GetValue(rules) ?? Array.Empty<string>()), level, new Suppressions());
                }
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine("netscope diff: error: " + ex.Message);
                code = Analyzer.ExitBadArguments;
                return;
            }

            var options = new DiffOptions
            {
                Filter = methodFilter,
                Pseudo = p.GetValue(pseudo),
                RefPaths = (p.GetValue(refPath) ?? Array.Empty<string>()).Select(Path.GetFullPath).ToList(),
                Auditor = auditor,
            };
            var result = new DiffResult();
            code = Compare(p.GetValue(oldArg)!, p.GetValue(newArg)!, p.GetValue(recursive), options, result);
            if (code is Analyzer.ExitBadArguments or Analyzer.ExitNothingOpened) return;

            var outPath = p.GetValue(output);
            bool color = outPath == null && p.GetValue(format) == "text" && !p.GetValue(noColor) && !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") == null;
            var text = p.GetValue(format) == "json" ? DiffReporter.Json(result) : DiffReporter.Text(result, color);
            if (outPath != null) File.WriteAllText(outPath, text, OutputSink.Utf8);
            else stdout.Write(text);

            if (p.GetValue(exitCode) && code == Analyzer.ExitOk) code = result.HasDifferences ? ExitDifferences : Analyzer.ExitOk;
        });
        parse.Invoke();
        return code;
    }

    private static int Compare(string oldPath, string newPath, bool recursive, DiffOptions options, DiffResult result)
    {
        bool oldDir = Directory.Exists(oldPath), newDir = Directory.Exists(newPath);
        if (oldDir != newDir)
        {
            Console.Error.WriteLine("netscope diff: error: compare two files or two directories");
            return Analyzer.ExitBadArguments;
        }
        if (!oldDir)
        {
            using var a = Load(oldPath);
            using var b = Load(newPath);
            if (a == null || b == null) return Analyzer.ExitNothingOpened;
            result.Assemblies.Add(new AssemblyComparer(options).Compare(a, b));
            return Analyzer.ExitOk;
        }

        var olds = Scan(oldPath, recursive);
        var news = Scan(newPath, recursive);
        try
        {
            foreach (var (name, a) in olds)
            {
                if (news.TryGetValue(name, out var b)) result.Assemblies.Add(new AssemblyComparer(options).Compare(a, b));
                else result.OnlyOld.Add(a.FilePath);
            }
            foreach (var (name, b) in news)
                if (!olds.ContainsKey(name)) result.OnlyNew.Add(b.FilePath);
        }
        finally
        {
            foreach (var a in olds.Values.Concat(news.Values)) a.Dispose();
        }
        if (olds.Count + news.Count == 0)
        {
            Console.Error.WriteLine("netscope diff: error: no managed assemblies in the directories");
            return Analyzer.ExitNothingOpened;
        }
        return Log.Failures > 0 ? Analyzer.ExitPartial : Analyzer.ExitOk;
    }

    private static LoadedAssembly? Load(string path)
    {
        var asm = new AssemblyLoader().TryLoad(0, path, out var reason);
        if (asm == null) Console.Error.WriteLine($"netscope diff: error: {path}: {reason}");
        return asm;
    }

    /// <summary>Managed .dll/.exe files of a directory by assembly name, in path order (the first of duplicates wins).</summary>
    private static SortedDictionary<string, LoadedAssembly> Scan(string dir, bool recursive)
    {
        var map = new SortedDictionary<string, LoadedAssembly>(StringComparer.OrdinalIgnoreCase);
        var files = Directory.EnumerateFiles(dir, "*.*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal);
        foreach (var f in files)
        {
            var asm = new AssemblyLoader().TryLoad(0, f, out _);
            if (asm == null) continue; // native files next to managed ones are normal in a bin directory
            if (!map.TryAdd(asm.Name, asm))
            {
                Log.Warning(f, $"another file of assembly {asm.Name} was already matched; ignored");
                asm.Dispose();
            }
        }
        return map;
    }
}
