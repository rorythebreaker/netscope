using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using NetScope.Catalog;
using NetScope.Cfg;
using NetScope.IL;
using NetScope.Loading;
using NetScope.Metadata;

namespace NetScope.Pseudo;

public sealed class PseudoResult
{
    public required string Text { get; init; }
    /// <summary>decompiler or fallback.</summary>
    public required string Source { get; init; }
    /// <summary>Why the decompiler was not used.</summary>
    public string? Reason { get; init; }
}

public interface IPseudoGenerator
{
    PseudoResult Generate(MethodModel method, MethodIL? il, ControlFlowGraph? cfg);
}

/// <summary>
/// C# pseudocode via ICSharpCode.Decompiler (one decompiler per assembly, used by one thread),
/// with a per-method time limit and a three-address fallback.
/// </summary>
public sealed class PseudoGenerator : IPseudoGenerator
{
    private readonly LoadedAssembly _asm;
    private readonly IAssemblyResolver _resolver;
    private readonly MemberResolver _members;
    private readonly bool _raw;
    private readonly TimeSpan _timeout;
    private CSharpDecompiler? _decompiler;
    private string? _initError;

    public PseudoGenerator(LoadedAssembly asm, IAssemblyResolver resolver, MemberResolver members, bool raw, TimeSpan timeout)
    {
        _asm = asm;
        _resolver = resolver;
        _members = members;
        _raw = raw;
        _timeout = timeout;
    }

    public static DecompilerSettings CreateSettings(bool raw)
    {
        var s = new DecompilerSettings(LanguageVersion.Latest)
        {
            ThrowOnAssemblyResolveErrors = false,
            ShowXmlDocumentation = false,
            UseDebugSymbols = false,
            LoadInMemory = false,
            // Keep LINQ as method calls: it mirrors the real call sites.
            QueryExpressions = false,
        };
        if (raw)
        {
            // Show compiler-generated shapes: closures, state machines and iterators as emitted.
            s.AnonymousMethods = false;
            s.AnonymousTypes = false;
            s.ExpressionTrees = false;
            s.YieldReturn = false;
            s.AsyncAwait = false;
            s.AsyncEnumerator = false;
            s.AwaitInCatchFinally = false;
            s.LocalFunctions = false;
            s.Discards = false;
        }
        return s;
    }

    private CSharpDecompiler? GetDecompiler()
    {
        if (_decompiler != null || _initError != null) return _decompiler;
        try
        {
            _decompiler = new CSharpDecompiler(_asm.DecompilerFile, _resolver, CreateSettings(_raw));
        }
        catch (Exception ex)
        {
            _initError = "decompiler cannot load the assembly: " + FirstLine(ex.Message);
        }
        return _decompiler;
    }

    public PseudoResult Generate(MethodModel method, MethodIL? il, ControlFlowGraph? cfg)
    {
        string reason;
        var decompiler = GetDecompiler();
        if (decompiler == null)
        {
            reason = _initError!;
        }
        else
        {
            using var cts = new CancellationTokenSource(_timeout);
            decompiler.CancellationToken = cts.Token;
            var task = Task.Run(() => decompiler.DecompileAsString(method.Handle), cts.Token);
            try
            {
                // The decompiler checks the token cooperatively; the extra wait covers code that does not.
                if (task.Wait(_timeout + TimeSpan.FromSeconds(2)))
                    return new PseudoResult { Text = Clean(task.Result), Source = "decompiler" };
                _decompiler = null; // still busy in the abandoned task: use a fresh instance next time
                reason = $"decompilation exceeded the time limit of {_timeout.TotalSeconds:0} s";
            }
            catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
            {
                reason = $"decompilation exceeded the time limit of {_timeout.TotalSeconds:0} s";
            }
            catch (AggregateException ex)
            {
                var inner = ex.InnerException ?? ex;
                reason = "decompiler failed: " + inner.GetType().Name + ": " + FirstLine(inner.Message);
            }
        }

        if (il == null)
        {
            return new PseudoResult { Text = method.Signature + ";", Source = "fallback", Reason = reason };
        }
        try
        {
            cfg ??= new CfgBuilder().Build(il, _members);
            return new PseudoResult { Text = FallbackPseudo.Render(il, cfg, _members), Source = "fallback", Reason = reason };
        }
        catch (Exception ex)
        {
            return new PseudoResult { Text = "// pseudocode unavailable", Source = "fallback", Reason = reason + "; fallback failed: " + FirstLine(ex.Message) };
        }
    }

    /// <summary>Drops the leading using directives emitted for a single member and expands tabs.</summary>
    internal static string Clean(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        int start = 0;
        while (start < lines.Count && (lines[start].StartsWith("using ", StringComparison.Ordinal) || lines[start].Trim().Length == 0)) start++;
        return string.Join(Environment.NewLine, lines.Skip(start).Select(l => l.Replace("\t", "    ").TrimEnd())).Trim();
    }

    private static string FirstLine(string s)
    {
        int nl = s.IndexOfAny(new[] { '\r', '\n' });
        return nl < 0 ? s : s.Substring(0, nl);
    }
}
