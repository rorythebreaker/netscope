using System.CommandLine;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetScope.Asm;
using NetScope.Catalog;
using NetScope.Cfg;
using NetScope.Diagnostics;
using NetScope.IL;
using NetScope.Loading;
using NetScope.Metadata;
using NetScope.Native;
using NetScope.Pipeline;
using NetScope.Pseudo;
using NetScope.Sandboxing;

namespace NetScope.Debugging;

/// <summary>
/// `netscope debug &lt;app.dll&gt; --allow-run [-- args]` (TZ-NetScope-2 §3): launches the assembly as a child
/// process under a read-only debugger, stops on its entry point and reports the call stack. The analyzer
/// process never loads the assembly; the debug API lives in the separate <see cref="DebugHost"/> process,
/// reached over pipes. Nothing is ever written back into the target.
///
/// Stage 2 scope: launch, stop on Main, <c>stack</c>, <c>continue</c>, <c>quit</c>, the §3.4 safety gates and
/// the session journal. Breakpoints, stepping and variable inspection (§3.3) come in Stage 3.
/// </summary>
public static class DebugCommand
{
    public const string Name = "debug";

    public static int Run(string[] args, TextWriter stdout)
    {
        var root = new RootCommand("Run an assembly under a read-only debugger: stop on its entry point and show the call stack. The assembly is executed, so --allow-run is required.");
        var fileArg = new Argument<string>("file") { Description = "Assembly to debug (.dll or apphost .exe)" };
        var passThrough = new Argument<string[]>("args") { Description = "Arguments passed to the program (after --)", Arity = ArgumentArity.ZeroOrMore };
        var allowRun = new Option<bool>("--allow-run") { Description = "Required: allow NetScope to run the assembly's code" };
        var script = new Option<string>("--script") { Description = "Run debugger commands from a file, then exit (no interaction)" };
        var format = new Option<string>("--format") { Description = "Output format for stops", DefaultValueFactory = _ => "text" };
        format.AcceptOnlyFromAmong("text", "json");
        var outOpt = new Option<string>("--out") { Description = "Write output to a file instead of stdout" };
        var runMemory = new Option<string>("--run-memory") { Description = "Memory limit of the debugged process (e.g. 512M, 2G)", DefaultValueFactory = _ => "1G" };
        var runTimeout = new Option<int>("--run-timeout") { Description = "Time limit for the session, seconds", DefaultValueFactory = _ => 600 };
        var allowChildren = new Option<bool>("--allow-child-processes") { Description = "Let the debugged process start child processes (off by default)" };
        var adminOverride = new Option<bool>("--i-know-running-as-admin") { Description = "Permit running while elevated (logged); refused otherwise" };
        var auditLog = new Option<string>("--audit-log") { Description = "Write the session journal to this file (in addition to the default location)" };
        var quiet = new Option<bool>("--quiet", "-q") { Description = "Only errors in stderr" };
        root.Arguments.Add(fileArg);
        root.Arguments.Add(passThrough);
        foreach (var o in new Option[] { allowRun, script, format, outOpt, runMemory, runTimeout, allowChildren, adminOverride, auditLog, quiet }) root.Options.Add(o);

        var parse = root.Parse(new[] { root.Name }.Concat(args).ToArray());
        if (parse.Errors.Count > 0)
        {
            foreach (var e in parse.Errors) Console.Error.WriteLine("netscope debug: error: " + e.Message);
            Console.Error.WriteLine("Try 'netscope debug --help' for more information.");
            return Analyzer.ExitBadArguments;
        }

        int code = Analyzer.ExitOk;
        root.SetAction(p =>
        {
            if (p.GetValue(quiet)) Log.Level = Verbosity.Quiet;
            var file = p.GetValue(fileArg)!;

            // §3.4.2 / §3.6: without --allow-run the command starts no process at all.
            if (!p.GetValue(allowRun))
            {
                Console.Error.WriteLine("netscope debug: error: refusing to run '" + file + "' without --allow-run.");
                Console.Error.WriteLine("The debugger executes the assembly's code. Re-run with --allow-run to consent.");
                code = Analyzer.ExitBadArguments;
                return;
            }

            // §3.4.1 / §3.6: refuse to run while elevated unless explicitly overridden.
            if (IsElevated() && !p.GetValue(adminOverride))
            {
                Console.Error.WriteLine("netscope debug: error: refusing to run as administrator/root (the debugged code would get the same rights).");
                Console.Error.WriteLine("Re-run as a normal user, or pass --i-know-running-as-admin to override (it is logged).");
                code = Analyzer.ExitBadArguments;
                return;
            }

            if (!File.Exists(file)) { Log.Error(file, "file not found"); code = Analyzer.ExitNothingOpened; return; }
            using (var asm = new AssemblyLoader().TryLoad(0, file, out var reason))
            {
                if (asm == null) { Log.Error(file, "not a managed assembly: " + reason); code = Analyzer.ExitNothingOpened; return; }
            }

            if (!Program.TryParseSize(p.GetValue(runMemory) ?? "1G", out long memoryLimit))
            {
                Console.Error.WriteLine("netscope debug: error: --run-memory expects a size such as 512M or 2G");
                code = Analyzer.ExitBadArguments;
                return;
            }

            string sha = Sha256(file);
            bool interactive = p.GetValue(script) == null && !Console.IsInputRedirected;

            using var audit = DebugAuditLog.Open(p.GetValue(auditLog));
            audit.Line("session", "start");
            audit.Line("user", Environment.UserName);
            audit.Line("file", Path.GetFullPath(file));
            audit.Line("sha256", sha);
            audit.Line("args", string.Join(' ', args));
            audit.Line("limits", $"memory={memoryLimit} bytes, child-processes={p.GetValue(allowChildren)}, kill-on-exit=true");
            if (IsElevated()) audit.Line("elevated", "yes (--i-know-running-as-admin)");

            // §3.4.2: show the SHA-256 and, interactively, require explicit consent.
            Console.Error.WriteLine($"About to run: {Path.GetFullPath(file)}");
            Console.Error.WriteLine($"  SHA-256: {sha}");
            Console.Error.WriteLine($"  Isolation: memory {memoryLimit / (1024 * 1024)} MiB, no child processes{(p.GetValue(allowChildren) ? " (relaxed)" : "")}, killed when NetScope exits.");
            if (interactive)
            {
                Console.Error.Write("Continue? [y/N] ");
                var answer = Console.ReadLine();
                if (!string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine("Aborted.");
                    audit.Line("session", "aborted by user at consent");
                    code = Analyzer.ExitBadArguments;
                    return;
                }
            }

            // §3.4.2: if the file changed between inspection and launch, abort.
            if (Sha256(file) != sha)
            {
                Console.Error.WriteLine("netscope debug: error: the file changed since it was inspected; aborting.");
                audit.Line("session", "aborted: file changed");
                code = Analyzer.ExitBadArguments;
                return;
            }

            var sink = p.GetValue(outOpt) is { } outPath ? new StreamWriter(outPath, false, OutputSink.Utf8) { AutoFlush = true } : stdout;
            try
            {
                using var session = new DebugSession(file, sha, p.GetValue(passThrough) ?? Array.Empty<string>(), memoryLimit, p.GetValue(allowChildren),
                                        p.GetValue(script), p.GetValue(format) ?? "text", interactive, TimeSpan.FromSeconds(Math.Max(1, p.GetValue(runTimeout))), audit, sink);
                code = session.Run();
            }
            finally
            {
                if (!ReferenceEquals(sink, stdout)) sink.Dispose();
            }
            audit.Line("session", "end exit=" + code);
        });

        parse.Invoke();
        return code;
    }

    private static string Sha256(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    private static bool IsElevated()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
        try { return Geteuid() == 0; } catch { return false; }
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint Geteuid();

    /// <summary>Drives the host over pipes and renders stops. Owns no debug API itself.</summary>
    private sealed class DebugSession : IDisposable
    {
        private readonly string _file, _sha, _script, _format;
        private readonly string[] _args;
        private readonly long _memory;
        private readonly bool _allowChildren, _interactive;
        private readonly TimeSpan _timeout;
        private readonly DebugAuditLog _audit;
        private readonly TextWriter _out;
        private readonly BlockingCollection<JsonElement> _events = new();
        private Process? _host;
        private IReadOnlyList<DebugFrame> _lastFrames = Array.Empty<DebugFrame>();

        // Static-analysis context (parent side): used to resolve breakpoint masks to method tokens and to
        // render il/cfg/pseudo of the stopped method. Reading metadata here is the analyzer's normal job; the
        // assembly is never executed in this process.
        private LoadedAssembly? _asm;
        private MemberResolver? _resolver;
        private IReadOnlyList<MethodModel>? _methods;
        private bool _staticTried;

        private enum CommandResult { Prompt, Resume, Quit }

        public DebugSession(string file, string sha, string[] args, long memory, bool allowChildren, string? script, string format, bool interactive, TimeSpan timeout, DebugAuditLog audit, TextWriter sink)
        {
            _file = file; _sha = sha; _args = args; _memory = memory; _allowChildren = allowChildren;
            _script = script ?? ""; _format = format; _interactive = interactive; _timeout = timeout; _audit = audit; _out = sink;
        }

        public void Dispose() { _events.Dispose(); _host?.Dispose(); }

        public int Run()
        {
            var tempDir = Sandbox.CreateWorkingDirectory();
            try
            {
                var config = new DebugHost.HostConfig(Path.GetFullPath(_file), SelfCommand.DotnetHost(), _args, tempDir, _memory, _allowChildren);
                var configFile = Path.Combine(tempDir, "debug-config.json");
                File.WriteAllText(configFile, config.ToJson());

                var self = SelfCommand.Locate();
                var psi = new ProcessStartInfo
                {
                    FileName = self.FileName,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardErrorEncoding = Encoding.UTF8,
                    StandardOutputEncoding = Encoding.UTF8,
                };
                foreach (var a in self.PrefixArguments) psi.ArgumentList.Add(a);
                psi.ArgumentList.Add(DebugProtocol.HostCommand);
                psi.ArgumentList.Add(configFile);

                _host = Process.Start(psi) ?? throw new InvalidOperationException("cannot start the debug host");
                // Events arrive on the host's stderr; the target's own output is on stdout.
                var reader = new Thread(() => ReadEvents(_host.StandardError)) { IsBackground = true };
                reader.Start();
                var outReader = new Thread(() => ForwardProgramOutput(_host.StandardOutput)) { IsBackground = true };
                outReader.Start();

                int exitCode = Drive();
                try { if (!_host.WaitForExit(2000)) _host.Kill(entireProcessTree: true); } catch { }
                return exitCode;
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        private void ReadEvents(TextReader stderr)
        {
            string? line;
            while ((line = stderr.ReadLine()) != null)
            {
                JsonElement e;
                try { e = DebugProtocol.Parse(line); } catch { Console.Error.WriteLine(line); continue; }
                if (e.TryGetProperty("event", out _)) _events.Add(e);
                else Console.Error.WriteLine(line);
            }
            _events.CompleteAdding();
        }

        private static void ForwardProgramOutput(TextReader stdout)
        {
            string? line;
            while ((line = stdout.ReadLine()) != null) Console.Error.WriteLine("[program] " + line);
        }

        private JsonElement? Wait()
        {
            try { return _events.Take(); } catch (InvalidOperationException) { return null; }
        }

        private void Send(string cmd) => SendJson(DebugProtocol.Command(cmd));

        private void SendJson(string json) { try { _host!.StandardInput.WriteLine(json); _host.StandardInput.Flush(); } catch { } }

        private int Drive()
        {
            var scriptCommands = _script.Length > 0 && File.Exists(_script)
                ? new Queue<string>(File.ReadAllLines(_script).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')))
                : null;

            while (true)
            {
                var ev = Wait();
                if (ev == null) return Analyzer.ExitOk; // host closed without an explicit exit event
                var e = ev.Value;
                switch (e.Text("event"))
                {
                    case "ready":
                        _audit.Line("pid", e.Number("pid").ToString());
                        break;
                    case "stopped":
                        RenderStop(e);
                        if (!HandleStop(scriptCommands)) { Send("quit"); }
                        break;
                    case "stack":
                        RenderStack(e.Frames());
                        break;
                    case "exited":
                        if (_format != "json") Write($"Program exited with code {e.Number("code")}.");
                        _audit.Line("target", "exited code=" + e.Number("code"));
                        return Analyzer.ExitOk;
                    case "error":
                        Console.Error.WriteLine("netscope debug: " + e.Text("message"));
                        _audit.Line("error", e.Text("message") ?? "");
                        break;
                }
            }
        }

        /// <summary>Decides what to do at a stop. Returns false to end the session.</summary>
        private bool HandleStop(Queue<string>? scriptCommands)
        {
            if (scriptCommands != null)
            {
                while (scriptCommands.Count > 0)
                {
                    switch (RunCommand(scriptCommands.Dequeue()))
                    {
                        case CommandResult.Resume: return true;
                        case CommandResult.Quit: return false;
                    }
                }
                return false; // script exhausted
            }
            if (!_interactive)
            {
                // Default non-interactive run: show the stack (already rendered) and continue to exit.
                Send("continue");
                return true;
            }
            while (true)
            {
                Console.Error.Write("(ns) ");
                var input = Console.ReadLine();
                if (input == null) return false;
                switch (RunCommand(input))
                {
                    case CommandResult.Resume: return true;
                    case CommandResult.Quit: return false;
                }
            }
        }

        /// <summary>One debugger command (§3.2). Query/view commands render in place and return Prompt;
        /// continue/step return Resume to let the driver await the next stop; quit returns Quit.</summary>
        private CommandResult RunCommand(string input)
        {
            input = input.Trim();
            if (input.Length == 0) return CommandResult.Prompt;
            var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var cmd = parts[0];
            var arg = parts.Length > 1 ? parts[1] : "";
            switch (cmd)
            {
                case "continue" or "c" or "run": Send("continue"); return CommandResult.Resume;
                case "step" or "s": SendJson(DebugProtocol.StepCommand("in")); return CommandResult.Resume;
                case "next" or "n": SendJson(DebugProtocol.StepCommand("over")); return CommandResult.Resume;
                case "out" or "finish": SendJson(DebugProtocol.StepCommand("out")); return CommandResult.Resume;
                case "stepi" or "si": SendJson(DebugProtocol.StepCommand("inst")); return CommandResult.Resume;
                case "quit" or "q" or "kill": Send("quit"); return CommandResult.Quit;
                case "stack" or "bt" or "where": RenderStack(_lastFrames); return CommandResult.Prompt;
                case "break" or "b": AddBreak(arg); return CommandResult.Prompt;
                case "breaks": Send("breaks"); RenderNext("breaks"); return CommandResult.Prompt;
                case "delete" or "d":
                    if (int.TryParse(arg, out int id)) { SendJson(DebugProtocol.IntCommand("delete", "id", id)); RenderNext("info"); }
                    else Console.Error.WriteLine("usage: delete <n>");
                    return CommandResult.Prompt;
                case "threads": Send("threads"); RenderNext("threads"); return CommandResult.Prompt;
                case "exceptions":
                    SendJson(DebugProtocol.BoolCommand("exceptions", "on", arg == "on")); RenderNext("info");
                    return CommandResult.Prompt;
                case "args": Send("args"); RenderNext("variables"); return CommandResult.Prompt;
                case "locals": Send("locals"); RenderNext("variables"); return CommandResult.Prompt;
                case "print" or "p":
                    if (arg.Length == 0) Console.Error.WriteLine("usage: print <name>[.field...]");
                    else { SendJson(DebugProtocol.PrintCommand(arg)); RenderNext("variables"); }
                    return CommandResult.Prompt;
                case "frame" or "f":
                    if (int.TryParse(arg, out int fn)) { SendJson(DebugProtocol.IntCommand("frame", "n", fn)); RenderNext("info"); }
                    else Console.Error.WriteLine("usage: frame <n>");
                    return CommandResult.Prompt;
                case "il" or "cfg" or "pseudo" or "asm": RenderCode(cmd); return CommandResult.Prompt;
                case "help" or "?": PrintHelp(); return CommandResult.Prompt;
                default:
                    Console.Error.WriteLine($"unknown command '{cmd}' — try 'help'");
                    return CommandResult.Prompt;
            }
        }

        /// <summary>Resolves a `mask[+IL_xxxx]` (as in --filter) to method tokens and sets a breakpoint on each.</summary>
        private void AddBreak(string arg)
        {
            if (arg.Length == 0) { Console.Error.WriteLine("usage: break <method-mask>[+IL_xxxx]  (file:line not supported in this stage)"); return; }
            int? offset = null;
            int plus = arg.IndexOf("+IL_", StringComparison.OrdinalIgnoreCase);
            string mask = arg;
            if (plus >= 0)
            {
                if (int.TryParse(arg[(plus + 4)..], System.Globalization.NumberStyles.HexNumber, null, out int off)) offset = off;
                mask = arg[..plus];
            }
            if (mask.Contains(':') && !mask.Contains("::")) { Console.Error.WriteLine("break <file>:<line> is not supported in this stage; use a method mask"); return; }

            if (!LoadStatics() || _asm == null || _methods == null) { Console.Error.WriteLine("cannot read the assembly to resolve the mask"); return; }
            MethodModel[] matched;
            try { var filter = new MethodFilter(new[] { mask }, Array.Empty<string>()); matched = _methods.Where(filter.Matches).ToArray(); }
            catch (ArgumentException ex) { Console.Error.WriteLine("bad mask: " + ex.Message); return; }
            if (matched.Length == 0) { Console.Error.WriteLine($"no method matches '{mask}'"); return; }
            foreach (var m in matched)
            {
                SendJson(DebugProtocol.BreakCommand(_asm.FileName, m.Token, offset));
                RenderNext("breakpoint-set");
            }
        }

        private void RenderCode(string kind)
        {
            if (_lastFrames.Count == 0) { Console.Error.WriteLine("not stopped in a managed method"); return; }
            var top = _lastFrames[0];
            if (top.Token == 0) { Console.Error.WriteLine("the current frame has no managed method"); return; }
            if (!LoadStatics() || _asm == null || _methods == null || _resolver == null) { Console.Error.WriteLine("cannot read the assembly for code view"); return; }
            var method = _methods.FirstOrDefault(m => m.Token == top.Token);
            if (method == null) { Console.Error.WriteLine("the current method is not in the debugged assembly (" + top.Module + ")"); return; }
            try
            {
                switch (kind)
                {
                    case "il":
                    {
                        var il = MethodIL.Load(method, _resolver, out var err, out _);
                        if (il == null) { Console.Error.WriteLine("no IL: " + err); return; }
                        var listing = new AsmProvider(_resolver, false, Array.Empty<string>(), TimeSpan.Zero).GetIL(il);
                        foreach (var l in listing.Lines) Write($"  IL_{l.Offset:X4}  {l.OpCode} {l.Operand}".TrimEnd());
                        break;
                    }
                    case "cfg":
                    {
                        var il = MethodIL.Load(method, _resolver, out var err, out _);
                        if (il == null) { Console.Error.WriteLine("no IL: " + err); return; }
                        var g = new CfgBuilder().Build(il, _resolver);
                        Write($"  {g.Blocks.Count} block(s), complexity {g.CyclomaticComplexity}");
                        foreach (var b in g.Blocks) Write($"  BB{b.Index}  IL_{b.StartOffset:X4}..IL_{b.EndOffset:X4}  -> {string.Join(", ", b.Successors.Select(e => "BB" + e.To.Index))}");
                        break;
                    }
                    case "pseudo":
                    {
                        var il = MethodIL.Load(method, _resolver, out _, out _);
                        using var refs = new ReferenceResolver(new[] { _asm }, Array.Empty<string>());
                        var gen = new PseudoGenerator(_asm, refs.For(_asm), _resolver, false, TimeSpan.FromSeconds(10), new AbandonedDecompilations(4));
                        var cfg = il != null ? new CfgBuilder().Build(il, _resolver) : null;
                        var result = gen.Generate(method, il, cfg);
                        foreach (var line in result.Text.Split('\n')) Write("  " + line.TrimEnd());
                        break;
                    }
                    default:
                        Console.Error.WriteLine("asm in the debugger (JITted native of the running method) is not available in this stage; use 'netscope <file> --native' for ReadyToRun/mixed code");
                        break;
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"cannot render {kind}: " + ex.Message); }
        }

        private bool LoadStatics()
        {
            if (_staticTried) return _asm != null;
            _staticTried = true;
            _asm = new AssemblyLoader().TryLoad(0, _file, out _);
            if (_asm == null) return false;
            _resolver = new MemberResolver(_asm.Metadata, _asm.Name);
            _methods = new MethodCatalog().Build(_asm, _resolver, MethodFilter.All);
            return true;
        }

        /// <summary>Waits for the host's reply to a query and renders it.</summary>
        private void RenderNext(string expected)
        {
            var ev = Wait();
            if (ev is not { } e) return;
            switch (e.Text("event"))
            {
                case "breakpoint-set": Write($"Breakpoint {e.Number("id")} at {e.Text("description")}"); break;
                case "breaks":
                    if (e.TryGetProperty("items", out var items))
                        foreach (var b in items.EnumerateArray()) Write($"  #{b.Number("id")}  {b.Text("description")}  {(b.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.True ? "active" : "disabled")}");
                    break;
                case "threads":
                    Write("Threads: " + string.Join(", ", e.TryGetProperty("ids", out var ids) ? ids.EnumerateArray().Select(x => (x.GetInt32() == e.Number("current") ? "*" : "") + x.GetInt32()) : Array.Empty<string>()));
                    break;
                case "info": Write(e.Text("text") ?? ""); break;
                case "variables":
                    if (e.TryGetProperty("items", out var vars))
                        foreach (var v in vars.EnumerateArray()) Write($"  {v.Text("name")} = {v.Text("value")}");
                    break;
                case "error": Console.Error.WriteLine("netscope debug: " + e.Text("message")); break;
            }
        }

        private static void PrintHelp() => Console.Error.WriteLine(
            "Commands: break <mask>[+IL_xxxx], breaks, delete <n>, continue, step, next, out, stepi, " +
            "stack, frame <n>, args, locals, print <name>[.field], threads, exceptions on|off, il, cfg, pseudo, quit.");

        private void RenderStop(JsonElement e)
        {
            var frames = e.Frames();
            _lastFrames = frames;
            _audit.Line("stop", $"{e.Text("reason")} thread={e.Number("thread")} frames={frames.Count}");
            if (_format == "json") { Write(RawJson(e)); return; }
            string where = frames.Count > 0 ? frames[0].Method : "?";
            Write($"Stopped ({e.Text("reason")}) at {where}:");
            RenderStack(frames);
        }

        private void RenderStack(IReadOnlyList<DebugFrame> frames)
        {
            if (_format == "json") { Write(DebugProtocol.Stack(frames)); return; }
            foreach (var f in frames)
            {
                string il = f.Il is int o ? $"  IL_{o:X4}" : "";
                string module = f.Module.Length > 0 ? $"  ({f.Module})" : "";
                Write($"  #{f.Index}  {f.Method}{il}{module}");
            }
        }

        private static string RawJson(JsonElement e) => e.GetRawText();

        private void Write(string line) => _out.WriteLine(line);
    }
}
