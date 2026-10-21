using System.CommandLine;
using System.Reflection;
using System.Runtime.InteropServices;
using NetScope.Catalog;
using NetScope.Diagnostics;
using NetScope.Input;
using NetScope.Native;
using NetScope.Pipeline;
using NetScope.Reporting;
using NetScope.CrossRefs;
using NetScope.Sandboxing;

namespace NetScope;

public static class Program
{
    public static string Version { get; } =
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";

    public static int Main(string[] args)
    {
        // Hidden mode used by --native --allow-run: runs in a separate process only.
        if (args.Length > 0 && args[0] == JitRunner.HelperCommand) return JitRunner.HelperMain(args);

        var stderr = new StreamWriter(Console.OpenStandardError(), OutputSink.Utf8) { AutoFlush = true };
        Console.SetError(stderr);
        Log.SetWriter(stderr);
        var stdout = new StreamWriter(Console.OpenStandardOutput(), OutputSink.Utf8, 1 << 16) { AutoFlush = false };
        Console.SetOut(stdout);
        if (!Console.IsOutputRedirected)
        {
            try { Console.OutputEncoding = OutputSink.Utf8; } catch { }
        }
        try
        {
            return Run(args, stdout);
        }
        finally
        {
            stdout.Flush();
        }
    }

    internal static int Run(string[] args, TextWriter stdout)
    {
        Log.Reset();
        var cli = new Cli();
        // The parser treats a first argument that looks like the tool itself (e.g. a file named "netscope")
        // as the command name and drops it. Passing the command name explicitly keeps every argument.
        var parse = cli.Root.Parse(new[] { cli.Root.Name }.Concat(args).ToArray());
        if (parse.Errors.Count > 0)
        {
            foreach (var e in parse.Errors) Console.Error.WriteLine("netscope: error: " + e.Message);
            Console.Error.WriteLine("Try 'netscope --help' for more information.");
            return Analyzer.ExitBadArguments;
        }
        cli.Root.SetAction(p => cli.Execute(p, args, stdout));
        return parse.Invoke();
    }

    private sealed class Cli
    {
        public readonly RootCommand Root = new("NetScope: static analyzer of .NET assemblies. For each method prints its kind, calls, branches, pseudocode and assembly code. The analyzed code is never executed unless --allow-run is given.");

        private readonly Argument<string[]> _files = new("files") { Description = "Files or masks (*.dll, bin\\Release\\*.exe). The file type is detected by its PE/CLR header.", Arity = ArgumentArity.ZeroOrMore };
        private readonly Option<string[]> _dir = new("--dir") { Description = "Analyze all .dll and .exe files in a directory" };
        private readonly Option<bool> _recursive = new("--recursive", "-r") { Description = "With --dir: include subdirectories" };
        private readonly Option<string[]> _inputList = new("--input-list") { Description = "Text file with one path or mask per line" };
        private readonly Option<string[]> _refPath = new("--ref-path") { Description = "Directory with dependencies (repeatable)" };
        private readonly Option<bool> _methods = new("--methods") { Description = "List methods with kind and signature (default)" };
        private readonly Option<bool> _calls = new("--calls") { Description = "Calls made by each method and its callers" };
        private readonly Option<bool> _callgraph = new("--callgraph") { Description = "Call graph between methods of all input files" };
        private readonly Option<bool> _cfg = new("--cfg") { Description = "Basic blocks, branches, loops, try/catch regions, complexity" };
        private readonly Option<bool> _pseudo = new("--pseudo") { Description = "C#-like pseudocode (ICSharpCode.Decompiler, with a simplified fallback)" };
        private readonly Option<bool> _il = new("--il") { Description = "IL listing" };
        private readonly Option<bool> _native = new("--native") { Description = "Native code: from the ReadyToRun image, or JIT output with --allow-run" };
        private readonly Option<bool> _allowRun = new("--allow-run") { Description = "Allow running code of the analyzed assembly in a helper process (needed for --native on IL-only assemblies)" };
        private readonly Option<bool> _all = new("--all") { Description = "Enable all output groups" };
        private readonly Option<string[]> _filter = new("--filter") { Description = "Select methods by full name mask, e.g. \"MyApp.Auth.*::Login*\" (repeatable)" };
        private readonly Option<string[]> _type = new("--type") { Description = "Select methods by kind: " + string.Join(", ", MethodFilter.TypeAliases.Keys) };
        private readonly Option<string> _format = new("--format") { Description = "Output format", DefaultValueFactory = _ => "text" };
        private readonly Option<string> _out = new("--out") { Description = "Write to a file, or to a directory (one report per assembly)" };
        private readonly Option<bool> _raw = new("--raw") { Description = "Do not restore lambdas, async methods and iterators in pseudocode" };
        private readonly Option<bool> _noColor = new("--no-color") { Description = "Disable colored output" };
        private readonly Option<bool> _quiet = new("--quiet", "-q") { Description = "Only errors in stderr" };
        private readonly Option<bool> _verbose = new("--verbose", "-v") { Description = "Progress messages in stderr" };
        private readonly Option<int> _threads = new("--threads") { Description = "Number of files processed in parallel", DefaultValueFactory = _ => Environment.ProcessorCount };
        private readonly Option<int> _decompileTimeout = new("--decompile-timeout") { Description = "Time limit for decompiling one method, seconds", DefaultValueFactory = _ => 20 };
        private readonly Option<int> _maxAbandoned = new("--max-abandoned") { Description = "After this many decompilations exceed the time limit, the remaining methods get simplified pseudocode", DefaultValueFactory = _ => 4 };
        private readonly Option<bool> _noPdb = new("--no-pdb") { Description = "Do not read debug symbols (PDB)" };
        private readonly Option<string[]> _pdbPath = new("--pdb-path") { Description = "Directory searched for .pdb files after the assembly's directory (repeatable)" };
        private readonly Option<bool> _source = new("--source") { Description = "Show source lines next to IL (needs a PDB)" };
        private readonly Option<string[]> _sourcePath = new("--source-path") { Description = "Directory searched for source files not found at the path in the PDB (repeatable)" };
        private readonly Option<string[]> _uses = new("--uses") { Description = "Select methods that call a method or type by mask, e.g. \"System.Diagnostics.Process::Start*\" (repeatable)" };
        private readonly Option<string[]> _findString = new("--find-string") { Description = "Select methods with a string literal containing the text, or matching a mask with * and ? (repeatable)" };
        private readonly Option<bool> _regex = new("--regex") { Description = "With --find-string: the text is a regular expression" };
        private readonly Option<bool> _fields = new("--fields") { Description = "Fields each method reads and writes" };
        private readonly Option<string[]> _fieldRefs = new("--field-refs") { Description = "Select methods that read or write a field by mask, e.g. \"MyApp.Db.User::<Name>k__BackingField\" (repeatable)" };
        private readonly Option<bool> _strings = new("--strings") { Description = "Table of all string literals and where they occur" };
        private readonly Option<string> _runMemory = new("--run-memory") { Description = "Memory limit of processes that run analyzed code, e.g. 512M, 2G (number without a suffix: megabytes)", DefaultValueFactory = _ => "1G" };
        private readonly Option<int> _runTimeout = new("--run-timeout") { Description = "Time limit for the --allow-run helper process, seconds", DefaultValueFactory = _ => 30 };

        public Cli()
        {
            _format.AcceptOnlyFromAmong("text", "json", "dot", "mermaid");
            Root.Arguments.Add(_files);
            foreach (var o in new Option[] { _dir, _recursive, _inputList, _refPath, _methods, _calls, _callgraph, _cfg, _pseudo, _il, _native, _allowRun, _all, _filter, _type, _format, _out, _raw, _noColor, _quiet, _verbose, _threads, _decompileTimeout, _maxAbandoned, _runMemory, _runTimeout,
                                             _noPdb, _pdbPath, _source, _sourcePath, _uses, _findString, _regex, _fields, _fieldRefs, _strings })
                Root.Options.Add(o);
        }

        public int Execute(ParseResult p, string[] args, TextWriter stdout)
        {
            Log.Level = p.GetValue(_quiet) ? Verbosity.Quiet : p.GetValue(_verbose) ? Verbosity.Verbose : Verbosity.Normal;

            var files = p.GetValue(_files) ?? Array.Empty<string>();
            var dirs = p.GetValue(_dir) ?? Array.Empty<string>();
            var lists = p.GetValue(_inputList) ?? Array.Empty<string>();
            // The parser passes unknown options through as file names; reject them.
            var unknown = files.FirstOrDefault(f => f.StartsWith('-') && f.Length > 1 && !File.Exists(f));
            if (unknown != null)
            {
                Console.Error.WriteLine($"netscope: error: unknown option '{unknown}'");
                Console.Error.WriteLine("Try 'netscope --help' for more information.");
                return Analyzer.ExitBadArguments;
            }
            if (files.Length + dirs.Length + lists.Length == 0)
            {
                Console.Error.WriteLine("netscope: error: no input files. Usage: netscope [options] <file|mask> [<file|mask> ...]");
                Console.Error.WriteLine("Try 'netscope --help' for more information.");
                return Analyzer.ExitBadArguments;
            }
            int threads = p.GetValue(_threads);
            int decompileTimeout = p.GetValue(_decompileTimeout);
            int runTimeout = p.GetValue(_runTimeout);
            int maxAbandoned = p.GetValue(_maxAbandoned);
            if (threads < 1 || decompileTimeout < 1 || runTimeout < 1 || maxAbandoned < 0)
            {
                Console.Error.WriteLine("netscope: error: --threads and timeouts must be positive numbers, --max-abandoned must not be negative");
                return Analyzer.ExitBadArguments;
            }

            bool all = p.GetValue(_all);
            bool calls = all || p.GetValue(_calls), cfg = all || p.GetValue(_cfg), pseudo = all || p.GetValue(_pseudo);
            bool il = all || p.GetValue(_il), native = all || p.GetValue(_native), callgraph = all || p.GetValue(_callgraph);
            bool fields = all || p.GetValue(_fields), strings = p.GetValue(_strings);
            bool anyGroup = calls || cfg || pseudo || il || native || callgraph || fields || strings;
            bool methods = all || p.GetValue(_methods) || !anyGroup;
            var groups = new OutputGroups(methods, calls, cfg, pseudo, il, native, callgraph) { Fields = fields, Strings = strings };

            CrossReferenceQuery query;
            try
            {
                query = new CrossReferenceQuery(p.GetValue(_uses) ?? Array.Empty<string>(), p.GetValue(_findString) ?? Array.Empty<string>(),
                                                p.GetValue(_regex), p.GetValue(_fieldRefs) ?? Array.Empty<string>());
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine("netscope: error: " + ex.Message);
                return Analyzer.ExitBadArguments;
            }
            if (p.GetValue(_regex) && (p.GetValue(_findString) ?? Array.Empty<string>()).Length == 0)
            {
                Console.Error.WriteLine("netscope: error: --regex applies to --find-string");
                return Analyzer.ExitBadArguments;
            }

            if (!TryParseSize(p.GetValue(_runMemory) ?? "1G", out long runMemory))
            {
                Console.Error.WriteLine("netscope: error: --run-memory expects a size such as 512M or 2G");
                return Analyzer.ExitBadArguments;
            }

            var types = p.GetValue(_type) ?? Array.Empty<string>();
            try
            {
                _ = new MethodFilter(Array.Empty<string>(), types);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine("netscope: error: " + ex.Message);
                return Analyzer.ExitBadArguments;
            }

            var format = p.GetValue(_format) ?? "text";
            var output = p.GetValue(_out);
            bool color = format == "text" && output == null && !p.GetValue(_noColor) && !Console.IsOutputRedirected &&
                         Environment.GetEnvironmentVariable("NO_COLOR") == null && EnableAnsi();

            var options = new AnalysisOptions
            {
                Inputs = OrderInputs(args, files, dirs, lists),
                Recursive = p.GetValue(_recursive),
                RefPaths = (p.GetValue(_refPath) ?? Array.Empty<string>()).Select(Path.GetFullPath).ToList(),
                Groups = groups,
                AllowRun = p.GetValue(_allowRun),
                Raw = p.GetValue(_raw),
                Filters = p.GetValue(_filter) ?? Array.Empty<string>(),
                Types = types,
                Format = format,
                Out = output,
                Color = color,
                Threads = threads,
                DecompileTimeout = TimeSpan.FromSeconds(decompileTimeout),
                MaxAbandoned = maxAbandoned,
                RunTimeout = TimeSpan.FromSeconds(runTimeout),
                Sandbox = new SandboxOptions { MemoryLimitBytes = runMemory },
                NoPdb = p.GetValue(_noPdb),
                PdbPaths = (p.GetValue(_pdbPath) ?? Array.Empty<string>()).Select(Path.GetFullPath).ToList(),
                ShowSource = p.GetValue(_source),
                SourcePaths = (p.GetValue(_sourcePath) ?? Array.Empty<string>()).Select(Path.GetFullPath).ToList(),
                Query = query,
            };
            if (options.ShowSource && options.NoPdb)
                Log.Warning("--source", "has no effect with --no-pdb");
            if (options.Groups.Native && !options.AllowRun)
                Log.Info("--native without --allow-run: only ReadyToRun code is shown");
            return new Analyzer().Run(options, stdout);
        }

        /// <summary>Keeps the command-line order of positional files, --dir and --input-list values.</summary>
        private static List<InputItem> OrderInputs(string[] args, string[] files, string[] dirs, string[] lists)
        {
            var valueOptions = new HashSet<string>(StringComparer.Ordinal)
            {
                "--dir", "--input-list", "--ref-path", "--filter", "--type", "--format", "--out", "--threads", "--decompile-timeout", "--max-abandoned", "--run-memory", "--run-timeout",
                "--pdb-path", "--source-path", "--uses", "--find-string", "--field-refs",
            };
            var items = new List<InputItem>();
            var pendingFiles = new Queue<string>(files);
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                string? name = null, value = null;
                int eq = a.IndexOf('=');
                if (a.StartsWith("--", StringComparison.Ordinal) && eq > 0) { name = a.Substring(0, eq); value = a.Substring(eq + 1); }
                else if (valueOptions.Contains(a) && i + 1 < args.Length) { name = a; value = args[++i]; }
                if (name != null)
                {
                    if (name == "--dir") items.Add(new InputItem(InputKind.Directory, value!));
                    else if (name == "--input-list") items.Add(new InputItem(InputKind.List, value!));
                    continue;
                }
                if (pendingFiles.Count > 0 && a == pendingFiles.Peek()) items.Add(new InputItem(InputKind.Path, pendingFiles.Dequeue()));
            }
            // Anything the scan could not place (e.g. response files) keeps the parser's order.
            foreach (var f in pendingFiles) items.Add(new InputItem(InputKind.Path, f));
            foreach (var d in dirs.Where(d => !items.Any(x => x.Kind == InputKind.Directory && x.Value == d))) items.Add(new InputItem(InputKind.Directory, d));
            foreach (var l in lists.Where(l => !items.Any(x => x.Kind == InputKind.List && x.Value == l))) items.Add(new InputItem(InputKind.List, l));
            return items;
        }
    }

    /// <summary>"1G", "512M", "64K", "1024" (megabytes); 0 disables the limit.</summary>
    internal static bool TryParseSize(string text, out long bytes)
    {
        bytes = 0;
        text = text.Trim();
        if (text.Length == 0) return false;
        long unit = 1L << 20;
        char last = char.ToUpperInvariant(text[^1]);
        if (last is 'K' or 'M' or 'G')
        {
            unit = last switch { 'K' => 1L << 10, 'M' => 1L << 20, _ => 1L << 30 };
            text = text[..^1];
        }
        if (!long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long n) || n > long.MaxValue / unit) return false;
        bytes = n * unit;
        return true;
    }

    /// <summary>Enables ANSI escape sequences on the Windows console; returns false if colors cannot be shown.</summary>
    private static bool EnableAnsi()
    {
        if (!OperatingSystem.IsWindows()) return Environment.GetEnvironmentVariable("TERM") != "dumb";
        try
        {
            var handle = GetStdHandle(-11);
            if (!GetConsoleMode(handle, out uint mode)) return false;
            return (mode & 4) != 0 || SetConsoleMode(handle, mode | 4);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr handle, uint mode);
}
