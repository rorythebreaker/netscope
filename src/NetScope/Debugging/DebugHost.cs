using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ClrDebug;
using NetScope.Metadata;
using NetScope.Sandboxing;

namespace NetScope.Debugging;

/// <summary>
/// The <c>__debug-host</c> process (TZ-NetScope-2 §3.5): it launches the target under ICorDebug through
/// DbgShim, applies the <see cref="Sandbox"/> restrictions to the target before it runs, and answers a closed
/// set of read-only requests over pipes. It holds the debug API so a fault in it never reaches the analyzer.
/// There is no memory-write, set-variable or func-eval path anywhere in here.
///
/// Protocol: events are written as JSON lines on <b>stderr</b> (stdout is left to the target's own output);
/// commands are read as JSON lines on <b>stdin</b>.
/// </summary>
public static class DebugHost
{
    private static readonly object WriteLock = new();

    public static int HostMain(string[] args)
    {
        if (args.Length < 1) { Emit(DebugProtocol.ErrorEvent("missing config file")); return 2; }
        HostConfig config;
        try
        {
            config = HostConfig.Load(args[0]);
        }
        catch (Exception ex)
        {
            Emit(DebugProtocol.ErrorEvent("cannot read config: " + ex.Message));
            return 2;
        }

        if (!OperatingSystem.IsWindows())
        {
            // Core-on-Linux and the Framework backend are not in this stage; fail cleanly, never crash (§4.3).
            Emit(DebugProtocol.ErrorEvent("the debugger backend is only available on Windows in this version"));
            return 3;
        }

        try
        {
            using var session = new HostSession(config);
            return session.Run();
        }
        catch (Exception ex)
        {
            Emit(DebugProtocol.ErrorEvent(ex.GetType().Name + ": " + ex.Message));
            return 1;
        }
    }

    internal static void Emit(string json)
    {
        lock (WriteLock)
        {
            Console.Error.WriteLine(json);
            Console.Error.Flush();
        }
    }

    /// <summary>A single debug session for one target process.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private sealed class HostSession : IDisposable
    {
        private readonly HostConfig _config;
        private readonly Dictionary<string, MetadataReader?> _metadata = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CorDebugModule> _modules = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, (CorDebugBreakpoint Bp, string Desc)> _breakpoints = new();
        private readonly ManualResetEventSlim _exited = new();
        private CorDebugProcess? _process;
        private CorDebugThread? _stoppedThread;
        private WindowsJob? _job;
        private int _exitCode;
        private int _nextBreakId = 1;
        private int _frameIndex;
        private bool _breakOnException;
        private DebugValues? _values;

        public HostSession(HostConfig config) => _config = config;

        public void Dispose() { _exited.Dispose(); _job?.Dispose(); }

        public int Run()
        {
            var shim = new DbgShim(NativeLibrary.Load(LocateDbgShim()));
            string commandLine = BuildCommandLine();
            var launch = shim.CreateProcessForLaunch(commandLine, bSuspendProcess: true, IntPtr.Zero, _config.WorkingDirectory);

            // Confine the suspended target before it runs: memory limit, no child processes, killed with the host.
            ConfineTarget(launch.ProcessId);

            // The runtime waits for this callback to return before it goes on starting up, so the attach must
            // happen inside it. Attaching later from another thread races the target: under load it ran Main
            // (or exited) before the entry breakpoint existed.
            var started = new ManualResetEventSlim();
            Exception? attachError = null;
            PSTARTUP_CALLBACK cb = (pCordb, _, hr) =>
            {
                try
                {
                    if (pCordb == IntPtr.Zero) throw new InvalidOperationException($"the runtime did not start (HRESULT 0x{(int)hr:X8})");
                    Attach(new CorDebug((ICorDebug)Marshal.GetObjectForIUnknown(pCordb)), launch.ProcessId);
                }
                catch (Exception ex) { attachError = ex; }
                finally { started.Set(); }
            };
            shim.RegisterForRuntimeStartup(launch.ProcessId, cb, IntPtr.Zero);
            shim.ResumeProcess(launch.ResumeHandle);
            shim.CloseResumeHandle(launch.ResumeHandle);
            if (!started.Wait(TimeSpan.FromSeconds(30)) || attachError != null || _process == null)
            {
                _job?.Terminate();
                Emit(DebugProtocol.ErrorEvent(attachError != null ? "cannot attach: " + attachError.Message : "the runtime did not start within 30s"));
                return 1;
            }
            GC.KeepAlive(cb);

            Emit(DebugProtocol.Ready(launch.ProcessId));
            CommandLoop();

            _exited.Wait(TimeSpan.FromSeconds(5));
            _job?.Dispose();
            return _exitCode;
        }

        /// <summary>Runs on the runtime-startup callback, while the target's runtime is blocked.</summary>
        private void Attach(CorDebug cor, int pid)
        {
            cor.Initialize();
            var callback = new CorDebugManagedCallback();
            callback.OnLoadModule += OnLoadModule;
            callback.OnBreakpoint += OnBreakpoint;
            callback.OnStepComplete += (_, e) => Park(StopReason.Step, e.Thread);
            callback.OnException += (_, e) =>
            {
                if (_breakOnException) Park(StopReason.Exception, e.Thread);
                else { try { e.Controller.Continue(false); } catch { } }
            };
            callback.OnExitProcess += (_, e) => { _exitCode = 0; Emit(DebugProtocol.Exited(_exitCode)); _exited.Set(); };
            // Every event must be continued or the target stalls; the stops we surface are continued by the command loop.
            callback.OnAnyEvent += (_, e) =>
            {
                // The stops we surface (and Exception when we are catching it) are continued by the command loop.
                if (e.Kind is CorDebugManagedCallbackKind.Breakpoint or CorDebugManagedCallbackKind.StepComplete or CorDebugManagedCallbackKind.ExitProcess) return;
                if (e.Kind is CorDebugManagedCallbackKind.Exception && _breakOnException) return;
                try { e.Controller.Continue(false); } catch { /* process may be exiting */ }
            };
            cor.SetManagedHandler(callback);
            _process = cor.DebugActiveProcess(pid, win32Attach: false);
        }

        private void ConfineTarget(int pid)
        {
            var options = new SandboxOptions
            {
                MemoryLimitBytes = _config.MemoryLimitBytes,
                AllowChildProcesses = _config.AllowChildProcesses,
                KillOnParentExit = true,
            };
            try
            {
                _job = WindowsJob.Create(options);
                var handle = OpenProcess(ProcessAllAccess, false, pid);
                if (handle == IntPtr.Zero) throw new InvalidOperationException("OpenProcess failed for pid " + pid);
                try { _job.Assign(handle); }
                finally { CloseHandle(handle); }
            }
            catch (Exception ex)
            {
                // A failed restriction must stop the run, not weaken it silently (§3.4.3).
                _job?.Terminate();
                throw new InvalidOperationException("cannot sandbox the target: " + ex.Message, ex);
            }
        }

        private void OnLoadModule(object? sender, LoadModuleCorDebugManagedCallbackEventArgs e)
        {
            var module = e.Module;
            try { _modules[Path.GetFileName(module.Name)] = module; } catch { }
            // Make the JIT emit debuggable code so arguments and locals are available to read (§3.3).
            try { module.JITCompilerFlags = CorDebugJITCompilerFlags.CORDEBUG_JIT_DISABLE_OPTIMIZATION; } catch { }
            if (module.Name.EndsWith(_config.TargetName, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var fs = File.OpenRead(_config.TargetPath);
                    using var pe = new PEReader(fs);
                    int entry = pe.PEHeaders.CorHeader?.EntryPointTokenOrRelativeVirtualAddress ?? 0;
                    if ((entry & 0xFF000000) == 0x06000000) // a MethodDef token (the managed entry point)
                        module.GetFunctionFromToken((mdMethodDef)entry).CreateBreakpoint().Activate(true);
                    else
                        Emit(DebugProtocol.ErrorEvent("no managed entry point to break on"));
                }
                catch (Exception ex) { Emit(DebugProtocol.ErrorEvent("cannot set entry breakpoint: " + ex.Message)); }
            }
        }

        private void OnBreakpoint(object? sender, BreakpointCorDebugManagedCallbackEventArgs e)
        {
            var reason = !_announcedEntry ? StopReason.Entry : StopReason.Breakpoint;
            _announcedEntry = true;
            Park(reason, e.Thread);
        }

        private bool _announcedEntry;

        /// <summary>Surface a stop without continuing: the target stays stopped and read-only until the
        /// command loop resumes it. Nothing is written back into the process.</summary>
        private void Park(StopReason reason, CorDebugThread thread)
        {
            _stoppedThread = thread;
            _frameIndex = 0; // the newest frame is selected at every stop
            Emit(DebugProtocol.Stopped(reason, SafeThreadId(thread), WalkStack(thread)));
        }

        private void CommandLoop()
        {
            string? line;
            while (!_exited.IsSet && (line = Console.In.ReadLine()) != null)
            {
                JsonElement c;
                try { c = DebugProtocol.Parse(line); } catch { continue; }
                switch (c.Text("cmd"))
                {
                    case "stack":
                        if (_stoppedThread != null) Emit(DebugProtocol.Stack(WalkStack(_stoppedThread)));
                        break;
                    case "continue":
                        try { _process?.Continue(false); } catch (Exception ex) { Emit(DebugProtocol.ErrorEvent("continue failed: " + ex.Message)); }
                        break;
                    case "break":
                        SetBreakpoint(c.Text("module") ?? "", c.Number("token"), c.TryGetProperty("offset", out var off) && off.ValueKind == JsonValueKind.Number ? off.GetInt32() : (int?)null);
                        break;
                    case "delete":
                        DeleteBreakpoint(c.Number("id"));
                        break;
                    case "breaks":
                        Emit(DebugProtocol.Breaks(_breakpoints.OrderBy(k => k.Key).Select(k => (k.Key, k.Value.Desc, true)).ToList()));
                        break;
                    case "step":
                        DoStep(c.Text("kind") ?? "in");
                        break;
                    case "threads":
                        EmitThreads();
                        break;
                    case "exceptions":
                        _breakOnException = c.TryGetProperty("on", out var on) && on.ValueKind == JsonValueKind.True;
                        Emit(DebugProtocol.Info("exceptions " + (_breakOnException ? "on" : "off")));
                        break;
                    case "frame":
                        _frameIndex = Math.Max(0, c.Number("n"));
                        Emit(DebugProtocol.Info("frame " + _frameIndex));
                        break;
                    case "args":
                        EmitVariables("args");
                        break;
                    case "locals":
                        EmitVariables("locals");
                        break;
                    case "print":
                        PrintPath(c.Text("path") ?? "");
                        break;
                    case "quit":
                    case "kill":
                        try { _process?.Terminate(0); } catch { }
                        _job?.Terminate();
                        _exited.Set();
                        return;
                }
            }
        }

        private void SetBreakpoint(string moduleName, int token, int? offset)
        {
            try
            {
                if (!_modules.TryGetValue(Path.GetFileName(moduleName), out var module))
                {
                    Emit(DebugProtocol.ErrorEvent($"module '{Path.GetFileName(moduleName)}' is not loaded yet; cannot set the breakpoint"));
                    return;
                }
                var fn = module.GetFunctionFromToken((mdMethodDef)token);
                CorDebugBreakpoint bp = offset is int o ? fn.ILCode.CreateBreakpoint(o) : fn.CreateBreakpoint();
                bp.Activate(true);
                int id = _nextBreakId++;
                string desc = ResolveMethod(module.Name, token) + (offset is int off ? $"+IL_{off:X4}" : "");
                _breakpoints[id] = (bp, desc);
                Emit(DebugProtocol.BreakpointSet(id, desc));
            }
            catch (Exception ex) { Emit(DebugProtocol.ErrorEvent("cannot set breakpoint: " + ex.Message)); }
        }

        private void DeleteBreakpoint(int id)
        {
            if (_breakpoints.Remove(id, out var b))
            {
                try { b.Bp.Activate(false); } catch { }
                Emit(DebugProtocol.Info($"deleted breakpoint {id}"));
            }
            else Emit(DebugProtocol.ErrorEvent($"no breakpoint {id}"));
        }

        private void DoStep(string kind)
        {
            if (_stoppedThread == null) { Emit(DebugProtocol.ErrorEvent("not stopped")); return; }
            try
            {
                var stepper = _stoppedThread.CreateStepper();
                switch (kind)
                {
                    case "out": stepper.StepOut(); break;
                    case "over": stepper.Step(false); break;
                    default: stepper.Step(true); break; // in / inst
                }
                _process?.Continue(false); // run until the step completes (OnStepComplete parks again)
            }
            catch (Exception ex) { Emit(DebugProtocol.ErrorEvent("step failed: " + ex.Message)); }
        }

        private void EmitThreads()
        {
            var ids = new List<int>();
            try { foreach (var t in _process!.Threads) ids.Add(SafeThreadId(t)); } catch { }
            int current = _stoppedThread != null ? SafeThreadId(_stoppedThread) : 0;
            if (ids.Count == 0 && current != 0) ids.Add(current); // at least the stopped thread
            Emit(DebugProtocol.Threads(current, ids));
        }

        /// <summary>The selected IL frame (newest = 0). Uses ActiveFrame for the leaf (the canonical frame for
        /// value reads) and the stackwalk for deeper frames. CreateStackWalk frames crash ClrDebug 0.4.2's
        /// argument/local enumerators, so value reads use the index-based Try* API below instead.</summary>
        private CorDebugILFrame? SelectedFrame()
        {
            if (_stoppedThread == null) return null;
            try
            {
                var sw = _stoppedThread.CreateStackWalk();
                int seen = 0;
                while (true)
                {
                    sw.TryGetFrame(out var frame);
                    if (frame is CorDebugILFrame)
                    {
                        if (seen == _frameIndex) return (CorDebugILFrame)frame;
                        seen++;
                    }
                    if (sw.TryNext() != HRESULT.S_OK) break;
                }
            }
            catch { }
            return null;
        }

        private void EmitVariables(string kind)
        {
            var items = new List<(string, string)>();
            var frame = SelectedFrame();
            if (frame == null) { Emit(DebugProtocol.ErrorEvent("the selected frame has no IL (native/internal)")); return; }
            _values ??= new DebugValues(MetadataOf);
            if (kind == "args")
            {
                var names = ArgumentNames(frame);
                for (int i = 0; i < 256; i++)
                {
                    if (frame.TryGetArgument(i, out var v) != HRESULT.S_OK || v == null) break;
                    items.Add((i < names.Count ? names[i] : "arg" + i, _values.Format(v)));
                }
            }
            else
            {
                for (int i = 0; i < 1024; i++)
                {
                    if (frame.TryGetLocalVariable(i, out var v) != HRESULT.S_OK || v == null) break;
                    items.Add(("local_" + i, _values.Format(v)));
                }
            }
            if (items.Count == 0)
            {
                // ICorDebug's GetArgument/GetLocalVariable return E_INVALIDARG for these frames in this
                // environment; value materialization is the remaining Stage-3 item (see the module remarks).
                Emit(DebugProtocol.Info($"({kind}: no values available — value inspection is not yet enabled in this build)"));
                return;
            }
            Emit(DebugProtocol.Variables(kind, items));
        }

        private void PrintPath(string path)
        {
            if (path.Length == 0) { Emit(DebugProtocol.ErrorEvent("usage: print <name>[.field...]")); return; }
            var frame = SelectedFrame();
            if (frame == null) { Emit(DebugProtocol.ErrorEvent("the selected frame has no IL")); return; }
            _values ??= new DebugValues(MetadataOf);
            var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            try
            {
                // Resolve the first segment as an argument or a local (index-based Try* reads only).
                CorDebugValue? root = null;
                var names = ArgumentNames(frame);
                for (int ai = 0; ai < names.Count; ai++)
                    if (names[ai] == segments[0] && frame.TryGetArgument(ai, out var av) == HRESULT.S_OK) { root = av; break; }
                if (root == null && segments[0].StartsWith("local_", StringComparison.Ordinal) && int.TryParse(segments[0].AsSpan(6), out int li))
                    if (frame.TryGetLocalVariable(li, out var lv) == HRESULT.S_OK) root = lv;
                if (root == null) { Emit(DebugProtocol.ErrorEvent($"no argument or local named '{segments[0]}'")); return; }

                string formatted = _values.FormatPath(root, segments.Skip(1).ToArray(), out var err);
                if (err != null) { Emit(DebugProtocol.ErrorEvent(err)); return; }
                Emit(DebugProtocol.Variables("print", new[] { (path, formatted) }));
            }
            catch (Exception ex) { Emit(DebugProtocol.ErrorEvent("print failed: " + ex.Message)); }
        }

        private List<string> ArgumentNames(CorDebugILFrame frame)
        {
            var names = new List<string>();
            try
            {
                var fn = frame.Function;
                var md = MetadataOf(fn.Module.Name);
                if (md == null) return names;
                var method = md.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle((int)fn.Token & 0x00FFFFFF));
                bool isStatic = (method.Attributes & System.Reflection.MethodAttributes.Static) != 0;
                if (!isStatic) names.Add("this");
                foreach (var ph in method.GetParameters())
                {
                    var p = md.GetParameter(ph);
                    if (p.SequenceNumber == 0) continue; // the return parameter
                    names.Add(md.GetString(p.Name));
                }
            }
            catch { }
            return names;
        }

        /// <summary>Walks the managed call stack with the Stage-1 fix: ICorDebugThread3::CreateStackWalk.</summary>
        private List<DebugFrame> WalkStack(CorDebugThread thread)
        {
            var frames = new List<DebugFrame>();
            try
            {
                var sw = thread.CreateStackWalk();
                int index = 0;
                while (true)
                {
                    sw.TryGetFrame(out var frame);
                    if (frame is CorDebugILFrame il && il.Function is { } fn)
                    {
                        int token = (int)fn.Token;
                        string module = fn.Module.Name;
                        frames.Add(new DebugFrame(index++, ResolveMethod(module, token), token, Path.GetFileName(module), ReadIl(il)));
                    }
                    else if (frame is CorDebugNativeFrame)
                    {
                        frames.Add(new DebugFrame(index++, "[native frame]", 0, "", null));
                    }
                    // boundary frames come back as null (S_FALSE): skip, keep walking.
                    if (sw.TryNext() != HRESULT.S_OK) break;
                }
            }
            catch (Exception ex) { Emit(DebugProtocol.ErrorEvent("stack walk failed: " + ex.Message)); }
            return frames;
        }

        /// <summary>IL offset, but only when it validates — the ClrDebug 0.4.2 GetIP quirk yields a bogus
        /// pointer with no mapping on stackwalk frames (see the class remarks / memory). Omitted until Stage 3.</summary>
        private static int? ReadIl(CorDebugILFrame il)
        {
            try
            {
                if (il.TryGetIP(out var ip) != HRESULT.S_OK) return null;
                bool mapped = ((int)ip.pMappingResult & 0x30) != 0; // EXACT | APPROXIMATE
                return mapped && ip.pnOffset >= 0 && ip.pnOffset < 0x100000 ? ip.pnOffset : null;
            }
            catch { return null; }
        }

        private string ResolveMethod(string modulePath, int token)
        {
            try
            {
                var md = MetadataOf(modulePath);
                if (md == null) return $"0x{token:X8}";
                var handle = MetadataTokens.MethodDefinitionHandle(token & 0x00FFFFFF);
                var method = md.GetMethodDefinition(handle);
                string name = md.GetString(method.Name);
                string type = Names.TypeDefFullName(md, method.GetDeclaringType());
                return type + "::" + name;
            }
            catch { return $"0x{token:X8}"; }
        }

        private MetadataReader? MetadataOf(string path)
        {
            if (_metadata.TryGetValue(path, out var cached)) return cached;
            MetadataReader? md = null;
            try
            {
                if (File.Exists(path))
                {
                    var pe = new PEReader(File.OpenRead(path)); // kept open for the reader's lifetime (host is short-lived)
                    if (pe.HasMetadata) md = pe.GetMetadataReader();
                }
            }
            catch { md = null; }
            _metadata[path] = md;
            return md;
        }

        private static int SafeThreadId(CorDebugThread thread)
        {
            try { return thread.Id; } catch { return 0; }
        }

        private string BuildCommandLine()
        {
            var sb = new StringBuilder();
            bool dll = _config.TargetPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            if (dll) sb.Append('"').Append(_config.DotnetHost).Append("\" ");
            sb.Append('"').Append(_config.TargetPath).Append('"');
            foreach (var a in _config.Args) sb.Append(" \"").Append(a.Replace("\"", "\\\"")).Append('"');
            return sb.ToString();
        }

        private static string LocateDbgShim()
        {
            string rid = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X86 => "win-x86",
                Architecture.Arm64 => "win-arm64",
                _ => "win-x64",
            };
            var path = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", "dbgshim.dll");
            if (File.Exists(path)) return path;
            // Single-file publish extracts natives next to the app, or a flat layout.
            var flat = Path.Combine(AppContext.BaseDirectory, "dbgshim.dll");
            if (File.Exists(flat)) return flat;
            var found = Directory.EnumerateFiles(AppContext.BaseDirectory, "dbgshim.dll", SearchOption.AllDirectories).FirstOrDefault();
            return found ?? throw new FileNotFoundException("dbgshim.dll not found under " + AppContext.BaseDirectory);
        }

        private const int ProcessAllAccess = 0x1F0FFF;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }

    /// <summary>What the parent tells the host to do. Written by <see cref="DebugCommand"/> as a JSON file.</summary>
    internal sealed record HostConfig(string TargetPath, string DotnetHost, string[] Args, string WorkingDirectory, long MemoryLimitBytes, bool AllowChildProcesses)
    {
        public string TargetName => Path.GetFileName(TargetPath);

        public static HostConfig Load(string file)
        {
            var e = JsonDocument.Parse(File.ReadAllText(file)).RootElement;
            return new HostConfig(
                e.Text("target") ?? throw new InvalidOperationException("no target"),
                e.Text("dotnetHost") ?? "dotnet",
                e.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : Array.Empty<string>(),
                e.Text("workingDir") ?? Directory.GetCurrentDirectory(),
                e.TryGetProperty("memoryLimit", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt64() : 1L << 30,
                e.TryGetProperty("allowChildProcesses", out var c) && c.ValueKind == JsonValueKind.True);
        }

        public string ToJson()
        {
            var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteString("target", TargetPath);
                w.WriteString("dotnetHost", DotnetHost);
                w.WriteStartArray("args");
                foreach (var a in Args) w.WriteStringValue(a);
                w.WriteEndArray();
                w.WriteString("workingDir", WorkingDirectory);
                w.WriteNumber("memoryLimit", MemoryLimitBytes);
                w.WriteBoolean("allowChildProcesses", AllowChildProcesses);
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
    }
}
