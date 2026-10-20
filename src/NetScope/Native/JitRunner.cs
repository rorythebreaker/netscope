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

            var listings = ParseListings(stdout.ToString());
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
                var key = JitKey(m.TypeName, m.Name);
                if (listings.TryGetValue(key, out var queue) && queue.Count > 0)
                {
                    var l = queue.Dequeue();
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
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("cannot locate the netscope executable");
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        var self = Path.GetFileNameWithoutExtension(exe);
        if (self.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            // Framework-dependent run: dotnet netscope.dll
            var dll = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrEmpty(dll)) throw new InvalidOperationException("cannot locate netscope.dll");
            psi.ArgumentList.Add(dll);
        }
        psi.ArgumentList.Add(HelperCommand);
        psi.ArgumentList.Add(requestFile);
        psi.ArgumentList.Add(resultFile);
        return psi;
    }

    private static string JitKey(string typeName, string methodName) => typeName + ":" + methodName;

    private sealed class Listing
    {
        public string? Architecture;
        public string? Tier;
        public int Size;
        public List<NativeLine> Lines = new();
    }

    /// <summary>Parses DOTNET_JitDisasm output into listings keyed by "Namespace.Type:Method".</summary>
    private static Dictionary<string, Queue<Listing>> ParseListings(string text)
    {
        var map = new Dictionary<string, Queue<Listing>>(StringComparer.Ordinal);
        Listing? current = null;
        const string Marker = "; Assembly listing for method ";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith(Marker, StringComparison.Ordinal))
            {
                current = new Listing();
                var name = line.Substring(Marker.Length);
                int paren = name.IndexOf('(');
                var head = paren < 0 ? name : name.Substring(0, paren);
                int tierStart = name.LastIndexOf(" (", StringComparison.Ordinal);
                if (tierStart > 0 && name.EndsWith(')')) current.Tier = name.Substring(tierStart + 2, name.Length - tierStart - 3);
                var key = StripGenericArgs(head);
                if (!map.TryGetValue(key, out var q)) map[key] = q = new Queue<Listing>();
                q.Enqueue(current);
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
            if (line.Length == 0 || line.StartsWith(";", StringComparison.Ordinal) && current.Lines.Count == 0) continue;
            current.Lines.Add(new NativeLine(0, "", line));
        }
        return map;
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
        int nl = s.IndexOfAny(new[] { '\r', '\n' });
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
