using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using NetScope.Catalog;

namespace NetScope.Native;

/// <summary>
/// Obtains JIT-compiled code for IL methods. This EXECUTES code of the analyzed assembly
/// (loading and compiling may run module initializers), so it is only used with --allow-run,
/// always in a separate helper process with a timeout. The main process never loads the assembly.
/// </summary>
public static class JitRunner
{
    public const string HelperCommand = "__jit-helper";

    public static Dictionary<MethodModel, NativeCode> Run(string assemblyPath, IReadOnlyList<MethodModel> methods, IReadOnlyList<string> refPaths, TimeSpan timeout)
    {
        var result = new Dictionary<MethodModel, NativeCode>();
        if (methods.Count == 0) return result;

        var tempDir = Path.Combine(Path.GetTempPath(), "netscope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var requestFile = Path.Combine(tempDir, "request.txt");
            var resultFile = Path.Combine(tempDir, "result.txt");
            var request = new StringBuilder();
            request.AppendLine(assemblyPath);
            request.AppendLine(string.Join("|", refPaths));
            foreach (var m in methods) request.AppendLine(m.Token.ToString("X8"));
            File.WriteAllText(requestFile, request.ToString());

            var psi = CreateStartInfo(requestFile, resultFile);
            // Only the target method names: other code JIT-ed in the helper is not listed.
            psi.Environment["DOTNET_JitDisasm"] = string.Join(" ", methods.Select(m => m.Name).Distinct(StringComparer.Ordinal));
            psi.Environment["DOTNET_JitDisasmDiffable"] = "0";
            psi.Environment["DOTNET_TieredCompilation"] = "0";
            psi.Environment["DOTNET_TieredPGO"] = "0";
            psi.Environment.Remove("DOTNET_JitStdOutFile"); // listings must go to the helper's stdout

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (stdout) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                foreach (var m in methods) result[m] = NativeCode.Unavailable($"helper process exceeded the time limit of {timeout.TotalSeconds:0} s");
                return result;
            }
            process.WaitForExit();

            if (!File.Exists(resultFile))
            {
                var why = stderr.ToString().Trim();
                foreach (var m in methods) result[m] = NativeCode.Unavailable("helper process failed" + (why.Length > 0 ? ": " + FirstLine(why) : $" (exit code {process.ExitCode})"));
                return result;
            }

            var lines = File.ReadAllLines(resultFile);
            string runtime = lines.Length > 0 ? lines[0] : "";
            var status = new Dictionary<int, string>();
            foreach (var line in lines.Skip(1))
            {
                var parts = line.Split('\t', 2);
                if (parts.Length == 2 && int.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out int token)) status[token] = parts[1];
            }

            var listings = ParseListings(stdout.ToString(), methods.ToDictionary(m => m.Token, m => JitKey(m.TypeName, m.Name)));
            foreach (var m in methods)
            {
                if (!status.TryGetValue(m.Token, out var s))
                {
                    result[m] = NativeCode.Unavailable("helper process did not compile the method");
                    continue;
                }
                if (s != "ok")
                {
                    result[m] = NativeCode.Unavailable(s);
                    continue;
                }
                // The listing printed after the method's token marker; the name key is the fallback.
                if (listings.ByToken.TryGetValue(m.Token, out var l) ||
                    listings.ByName.TryGetValue(JitKey(m.TypeName, m.Name), out var queue) && queue.TryDequeue(out l))
                {
                    result[m] = new NativeCode
                    {
                        Available = true,
                        Source = "JIT",
                        Architecture = l.Architecture ?? RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
                        RuntimeVersion = runtime,
                        Tier = l.Tier,
                        Size = l.Size,
                        Lines = l.Lines,
                    };
                }
                else result[m] = NativeCode.Unavailable("JIT produced no listing for the method (inlined-only or not compiled)");
            }
            return result;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static ProcessStartInfo CreateStartInfo(string requestFile, string resultFile)
    {
        var self = SelfCommand.Locate();
        var psi = new ProcessStartInfo
        {
            FileName = self.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in self.PrefixArguments) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(HelperCommand);
        psi.ArgumentList.Add(requestFile);
        psi.ArgumentList.Add(resultFile);
        return psi;
    }

    private static string JitKey(string typeName, string methodName) => typeName + ":" + methodName;

    internal sealed class Listing
    {
        public int? Token;
        public string? Architecture;
        public string? Tier;
        public int Size;
        public List<NativeLine> Lines = new();
    }

    /// <summary>Printed by the helper before each method it compiles: "##NETSCOPE-TOKEN 06000012".</summary>
    internal const string TokenMarker = "##NETSCOPE-TOKEN ";

    internal sealed class ParsedListings
    {
        /// <summary>The first listing for the method's own name after its token marker.</summary>
        public Dictionary<int, Listing> ByToken { get; } = new();
        /// <summary>All listings by "Namespace.Type:Method", in output order (fallback when no marker matches).</summary>
        public Dictionary<string, Queue<Listing>> ByName { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Parses DOTNET_JitDisasm output. A listing belongs to the last token marker before it, provided the
    /// listed method has the expected name (other methods JIT-ed meanwhile, e.g. by a type initializer, are skipped).
    /// </summary>
    internal static ParsedListings ParseListings(string text, IReadOnlyDictionary<int, string> expectedKeys)
    {
        var parsed = new ParsedListings();
        Listing? current = null;
        int? token = null;
        const string Marker = "; Assembly listing for method ";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith(TokenMarker, StringComparison.Ordinal))
            {
                current = null;
                token = int.TryParse(line.AsSpan(TokenMarker.Length).Trim(), System.Globalization.NumberStyles.HexNumber, null, out int t) ? t : null;
                continue;
            }
            if (line.StartsWith(Marker, StringComparison.Ordinal))
            {
                current = new Listing();
                var name = line.Substring(Marker.Length);
                int paren = name.IndexOf('(', StringComparison.Ordinal);
                var head = paren < 0 ? name : name.Substring(0, paren);
                int tierStart = name.LastIndexOf(" (", StringComparison.Ordinal);
                if (tierStart > 0 && name.EndsWith(')')) current.Tier = name.Substring(tierStart + 2, name.Length - tierStart - 3);
                var key = StripGenericArgs(head);
                if (!parsed.ByName.TryGetValue(key, out var q)) parsed.ByName[key] = q = new Queue<Listing>();
                q.Enqueue(current);
                if (token is int tk && expectedKeys.TryGetValue(tk, out var expected) && expected == key && !parsed.ByToken.ContainsKey(tk))
                {
                    parsed.ByToken[tk] = current;
                    current.Token = tk;
                }
                continue;
            }
            if (current == null) continue;
            if (line.StartsWith("; Emitting ", StringComparison.Ordinal))
            {
                var words = line.Split(' ');
                int forIdx = Array.IndexOf(words, "for");
                if (forIdx >= 0 && forIdx + 1 < words.Length) current.Architecture = words[forIdx + 1].ToLowerInvariant();
            }
            else if (line.StartsWith("; Tier", StringComparison.Ordinal) || line.StartsWith("; FullOpts", StringComparison.Ordinal) ||
                     line.StartsWith("; optimized", StringComparison.Ordinal) || line.StartsWith("; MinOpts", StringComparison.Ordinal))
            {
                current.Tier ??= line.Substring(2);
            }
            if (line.StartsWith("; Total bytes of code", StringComparison.Ordinal))
            {
                var digits = new string(line.Where(char.IsDigit).ToArray());
                if (int.TryParse(digits, out var size)) current.Size = size;
                current = null;
                continue;
            }
            if (line.Length == 0 || line.StartsWith(';') && current.Lines.Count == 0) continue;
            current.Lines.Add(new NativeLine(0, "", line));
        }
        // A listing claimed by a token is not handed out again by the name fallback.
        foreach (var q in parsed.ByName.Values)
        {
            var rest = q.Where(l => l.Token == null).ToList();
            q.Clear();
            foreach (var l in rest) q.Enqueue(l);
        }
        return parsed;
    }

    /// <summary>"List`1[System.__Canon]:Add[System.__Canon]" -> "List`1:Add".</summary>
    private static string StripGenericArgs(string s)
    {
        var sb = new StringBuilder(s.Length);
        int depth = 0;
        foreach (var c in s)
        {
            if (c == '[') depth++;
            else if (c == ']') depth--;
            else if (depth == 0) sb.Append(c);
        }
        return sb.ToString();
    }

    private static string FirstLine(string s)
    {
        int nl = s.AsSpan().IndexOfAny('\r', '\n');
        return nl < 0 ? s : s.Substring(0, nl);
    }

    // ------------------------------------------------------------------ helper process side

    /// <summary>Entry point of the helper process: loads the assembly and JIT-compiles the requested methods.</summary>
    public static int HelperMain(string[] args)
    {
        if (args.Length < 3) return 2;
        var request = File.ReadAllLines(args[1]);
        var assemblyPath = request[0];
        var refPaths = request.Length > 1 ? request[1].Split('|', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
        var output = new StringBuilder();
        output.AppendLine(RuntimeInformation.FrameworkDescription + " " + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant());

        var context = new HelperLoadContext(assemblyPath, refPaths);
        Assembly asm;
        try
        {
            asm = context.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
        }
        catch (Exception ex)
        {
            foreach (var t in request.Skip(2)) output.AppendLine(t + "\tcannot load assembly for JIT: " + ex.Message.Replace('\n', ' '));
            File.WriteAllText(args[2], output.ToString());
            return 0;
        }

        foreach (var tokenText in request.Skip(2))
        {
            if (!int.TryParse(tokenText, System.Globalization.NumberStyles.HexNumber, null, out int token)) continue;
            string status;
            try
            {
                var method = asm.ManifestModule.ResolveMethod(token) ?? throw new InvalidOperationException("method not found");
                RuntimeMethodHandle handle = method.MethodHandle;
                var typeArgs = method.DeclaringType is { IsGenericTypeDefinition: true } dt ? dt.GetGenericArguments().Length : 0;
                var methodArgs = method.IsGenericMethodDefinition ? method.GetGenericArguments().Length : 0;
                // Listings that follow belong to this token (the JIT and Console share the stdout pipe).
                FlushNativeStdout();
                Console.Out.Write(TokenMarker + tokenText + "\n");
                Console.Out.Flush();
                if (typeArgs + methodArgs > 0)
                {
                    // Shared canonical code (System.__Canon) for reference-type instantiations.
                    var inst = Enumerable.Repeat(typeof(object).TypeHandle, typeArgs + methodArgs).ToArray();
                    RuntimeHelpers.PrepareMethod(handle, inst);
                }
                else RuntimeHelpers.PrepareMethod(handle);
                status = "ok";
            }
            catch (Exception ex)
            {
                status = "JIT failed: " + (ex.InnerException ?? ex).Message.Replace('\n', ' ').Replace('\r', ' ');
            }
            output.Append(tokenText).Append('\t').AppendLine(status);
        }
        Console.Out.Flush();
        File.WriteAllText(args[2], output.ToString());
        return 0;
    }

    /// <summary>Flushes the C runtime's stdout buffer, where the JIT writes its listings.</summary>
    private static void FlushNativeStdout()
    {
        try
        {
            if (OperatingSystem.IsWindows()) _ = WindowsFlush(IntPtr.Zero);
            else _ = UnixFlush(IntPtr.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Listings still arrive; only the token binding may fall back to names.
        }
    }

    [DllImport("ucrtbase.dll", EntryPoint = "fflush")]
    private static extern int WindowsFlush(IntPtr stream);

    [DllImport("libc", EntryPoint = "fflush")]
    private static extern int UnixFlush(IntPtr stream);

    private sealed class HelperLoadContext : AssemblyLoadContext
    {
        private readonly string[] _dirs;

        public HelperLoadContext(string mainPath, string[] refPaths) : base("netscope-jit", isCollectible: false)
        {
            _dirs = new[] { Path.GetDirectoryName(Path.GetFullPath(mainPath)) ?? "." }.Concat(refPaths).ToArray();
        }

        protected override Assembly? Load(AssemblyName name)
        {
            // Framework assemblies always come from the default context.
            try { return Default.LoadFromAssemblyName(name); }
            catch { }
            foreach (var d in _dirs)
            {
                var p = Path.Combine(d, name.Name + ".dll");
                if (File.Exists(p))
                {
                    try { return LoadFromAssemblyPath(Path.GetFullPath(p)); }
                    catch { }
                }
            }
            return null; // framework assemblies come from the default context
        }
    }
}
